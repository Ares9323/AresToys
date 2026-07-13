using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using AresToys.App.Resources;
using AresToys.Core.Pipeline;
using AresToys.Pipeline;
using AresToys.Pipeline.Profiles;
using AresToys.Storage.Settings;
// Alias so we can keep `MenuItem` short while using WPF-UI's Fluent-styled subclass instead
// of System.Windows.Controls.MenuItem. The default WPF MenuItem template is rendered by the
// Aero/Aero2 theme dictionaries, which use internal brush keys we can't override from
// outside — even after setting ContextMenuBackground / SystemColors.MenuBrushKey at App
// level the submenu popup chrome stays on the system dark default. WPF-UI's MenuItem uses
// its own Fluent template that resolves through ContextMenuBackground / Surface* keys we
// already pin in ThemeService, so the menu themes correctly without any per-control work.
using MenuItem = Wpf.Ui.Controls.MenuItem;

namespace AresToys.App.Services;

public sealed class TrayIconService : IDisposable
{
    public const string LeftClickKey  = "tray.left_click";
    public const string DoubleClickKey = "tray.double_click";
    public const string MiddleClickKey = "tray.middle_click";

    /// <summary>Sentinel value persisted when the user picks "(do nothing)" — empty string
    /// would round-trip to "use default" so we use a fixed marker instead.</summary>
    public const string NoneMarker = "__none__";

    private readonly ILogger<TrayIconService> _logger;
    private readonly IServiceProvider _services;
    private readonly ISettingsStore _settings;
    private readonly IPipelineProfileStore _profiles;
    private readonly PipelineExecutor _executor;
    private readonly Hotkeys.HotkeyConfigService _hotkeys;
    private readonly LocalizationService _localization;
    private readonly ModuleSettings _modules;
    private readonly ElevationService _elevation;
    private readonly TaskbarIcon _icon;
    private MainWindow? _mainWindow;
    // Click handler bound to the most recently shown toast. Cleared after the toast closes
    // so a click on a stale balloon (which Windows still routes through after dismissal) is a no-op.
    private Action? _pendingToastClick;

    public TrayIconService(
        IServiceProvider services,
        ISettingsStore settings,
        IPipelineProfileStore profiles,
        PipelineExecutor executor,
        Hotkeys.HotkeyConfigService hotkeys,
        LocalizationService localization,
        ModuleSettings modules,
        ElevationService elevation,
        ILogger<TrayIconService> logger)
    {
        _services = services;
        _settings = settings;
        _profiles = profiles;
        _executor = executor;
        _hotkeys = hotkeys;
        _localization = localization;
        _modules = modules;
        _elevation = elevation;
        _logger = logger;
        _icon = new TaskbarIcon
        {
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/icon.ico", UriKind.Absolute)),
            ToolTipText = "AresToys",
            Visibility = Visibility.Visible
        };

        // Rebuild the menu whenever the user rebinds a hotkey so the "\tCtrl+Alt+R" suffixes
        // next to Region / Recording / Color sampler / etc. stay in sync. ContextMenu can be
        // swapped wholesale — H.NotifyIcon doesn't cache anything off it. Marshal to UI thread
        // because Changed fires from whichever thread did the rebind.
        _hotkeys.Changed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(
            new Action(() => _icon.ContextMenu = BuildMenu()));

        // Same rebuild on UI-language switch — the labels we set up below come from Strings.*
        // which respects CurrentUICulture, so a culture flip needs the menu to be re-rendered
        // for the new strings to show. Already-open submenus won't re-translate live; that's
        // the trade-off captured in Settings_LanguageHint.
        _localization.CultureChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(
            new Action(() => _icon.ContextMenu = BuildMenu()));

        _icon.ContextMenu = BuildMenu();
        // Click routing reads from settings on every event so changes apply without restart.
        // Defaults map to the historical behaviour: left=open Settings, double=toggle popup, middle=nothing.
        // When the clipboard module is disabled the default double-click target would lead nowhere,
        // so we fall back to opening Settings — preserves a useful gesture instead of dead silence.
        _icon.LeftClickCommand = new RelayCommand(() => DispatchClick(LeftClickKey, DefaultPipelineProfiles.OpenSettingsId));
        var doubleClickDefault = _modules.ClipboardEnabled
            ? DefaultPipelineProfiles.ShowPopupId
            : DefaultPipelineProfiles.OpenSettingsId;
        _icon.DoubleClickCommand = new RelayCommand(() => DispatchClick(DoubleClickKey, doubleClickDefault));
        _icon.MiddleClickCommand = new RelayCommand(() => DispatchClick(MiddleClickKey, NoneMarker));

        // ShowNotification() requires the underlying Win32 NOTIFYICONDATA to be created.
        // The TaskbarIcon WPF wrapper sometimes defers this until first message; force it now.
        _icon.ForceCreate();

        _icon.TrayBalloonTipClicked += OnTrayBalloonTipClicked;
        _icon.TrayBalloonTipClosed += (_, _) => _pendingToastClick = null;
    }

    /// <summary>Resolve the persisted profile id from settings (fall back to <paramref name="defaultProfileId"/>
    /// when unset). Empty / NoneMarker → no-op. Otherwise look up the profile + run it via the
    /// shared PipelineExecutor — same path every workflow takes, so a tray click can fire any
    /// of them. Synchronous read against the settings store is sub-ms (SQLite key-value).</summary>
    private void DispatchClick(string key, string defaultProfileId)
    {
        try
        {
            var raw = _settings.GetAsync(key, CancellationToken.None).GetAwaiter().GetResult();
            var profileId = string.IsNullOrEmpty(raw) ? defaultProfileId : raw;
            if (string.Equals(profileId, NoneMarker, StringComparison.Ordinal)) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    var profile = await _profiles.GetAsync(profileId, CancellationToken.None).ConfigureAwait(false);
                    if (profile is null)
                    {
                        _logger.LogWarning("Tray click: profile '{Id}' not found, ignoring", profileId);
                        return;
                    }
                    var ctx = new PipelineContext(_services);
                    await _executor.RunAsync(profile, ctx, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Tray click: workflow {Id} failed", profileId);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tray click: dispatch failed for {Key}", key);
        }
    }

    /// <summary>Run a built-in pipeline profile by id straight through the shared executor. Used
    /// by tray entries whose action IS a full workflow (e.g. Scan QR in region: region capture →
    /// decode → history) rather than a single service call. Mirrors the executor path
    /// <see cref="DispatchClick"/> takes, minus the settings indirection (the id is fixed).</summary>
    private void RunProfile(string profileId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var profile = await _profiles.GetAsync(profileId, CancellationToken.None).ConfigureAwait(false);
                if (profile is null)
                {
                    _logger.LogWarning("Tray: profile '{Id}' not found", profileId);
                    return;
                }
                var ctx = new PipelineContext(_services);
                await _executor.RunAsync(profile, ctx, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tray: profile {Id} failed", profileId);
            }
        });
    }

    public void Attach(MainWindow window) => _mainWindow = window;

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        // Capture submenu — mirrors ShareX's top-level Capture menu.
        var capture = new MenuItem { Header = Strings.Tray_Capture };
        capture.Items.Add(BuildMenuItem(Strings.Tray_Fullscreen,
            () => Run<CaptureCoordinator>(c => _ = c.CaptureFullscreenAsync(CancellationToken.None))));
        capture.Items.Add(BuildMonitorSubmenu());
        // "Active window" is only useful via hotkey — invoking it from the tray makes AresToys
        // itself the foreground window (the menu IS AresToys), so the capture filter excludes
        // every candidate and the operation silently no-ops. Available as a hotkey-bindable
        // workflow in Settings → Hotkeys for users who want it.
        capture.Items.Add(BuildShortcutMenuItem(Strings.Tray_Region, DefaultPipelineProfiles.RegionCaptureId,
            () => Run<CaptureCoordinator>(c => _ = c.CaptureRegionAsync(CancellationToken.None))));
        capture.Items.Add(BuildMenuItem(Strings.Tray_LastRegion,
            () => Run<CaptureCoordinator>(c => _ = c.CaptureLastRegionAsync(CancellationToken.None))));
        // Webpage capture needs WebView2 Runtime. When missing (old Win10 builds without the
        // bundled runtime, stripped LTSC images), swap the action for "(install runtime)" that
        // opens the Microsoft download page rather than firing a capture that would just abort.
        var webView2 = _services.GetService(typeof(WebView2AvailabilityService)) as WebView2AvailabilityService;
        if (webView2?.IsAvailable ?? false)
        {
            capture.Items.Add(BuildMenuItem(Strings.Tray_Webpage,
                () => Run<CaptureCoordinator>(c => _ = c.CaptureWebpageAsync(CancellationToken.None))));
        }
        else
        {
            capture.Items.Add(BuildMenuItem(Strings.Tray_WebpageInstallRuntime,
                () => webView2?.OpenInstallerPage()));
        }
        capture.Items.Add(new Separator());
        capture.Items.Add(BuildShortcutMenuItem(Strings.Tray_ScreenRecording, DefaultPipelineProfiles.RecordScreenMp4Id,
            () => Run<Services.Recording.RecordingCoordinator>(c => _ = c.ToggleAsync(AresToys.Capture.Recording.RecordingFormat.Mp4, CancellationToken.None))));
        capture.Items.Add(BuildShortcutMenuItem(Strings.Tray_ScreenRecordingGif, DefaultPipelineProfiles.RecordScreenGifId,
            () => Run<Services.Recording.RecordingCoordinator>(c => _ = c.ToggleAsync(AresToys.Capture.Recording.RecordingFormat.Gif, CancellationToken.None))));
        // Color Sampler / Color Picker live under Tools — they're tools, not captures.
        menu.Items.Add(capture);

        // Upload submenu — kicks off the manual-upload pipeline from arbitrary sources.
        var upload = new MenuItem { Header = Strings.Tray_Upload };
        upload.Items.Add(BuildMenuItem(Strings.Tray_UploadFile, OnUploadFile));
        upload.Items.Add(BuildMenuItem(Strings.Tray_UploadFromClipboard, OnUploadFromClipboard));
        // Upload text/URL go in a later sprint (need a text-input dialog).
        menu.Items.Add(upload);

        // Tools submenu — placeholder; we'll grow this with QR / hash / ruler / etc.
        var tools = new MenuItem { Header = Strings.Tray_Tools };
        tools.Items.Add(BuildShortcutMenuItem(Strings.Tray_ColorSampler, DefaultPipelineProfiles.ColorSamplerId,
            () => Run<ScreenColorPickerService>(s => s.PickAtCursor())));
        tools.Items.Add(BuildShortcutMenuItem(Strings.Tray_ColorPicker, DefaultPipelineProfiles.ColorPickerId,
            () => Run<ColorWheelLauncher>(l => _ = l.ShowAsync())));
        tools.Items.Add(BuildMenuItem(Strings.Tray_PinToScreen,
            () => Run<PinToScreenLauncher>(p => _ = p.ShowAsync(CancellationToken.None))));
        tools.Items.Add(BuildMenuItem(Strings.Tray_QrGenerator,
            () => Run<AresToys.App.Services.Qr.QrCodeService>(qr =>
                  Run<AresToys.Storage.Settings.ISettingsStore>(settings =>
                  Run<AresToys.App.Services.ManualUploadService>(ingestion =>
            {
                var win = new AresToys.App.Views.QrGeneratorWindow(qr, null, settings, ingestion);
                win.Show();
                win.Activate();
            })))));
        // Scan QR in region: region picker → ZXing decode → history + clipboard + toast. It's a
        // full pipeline (not a single service call), so dispatch the built-in profile straight
        // through the executor. Safe from the tray because the action starts with a region
        // overlay — foreground doesn't matter, unlike "Active window" capture.
        tools.Items.Add(BuildShortcutMenuItem(Strings.Tray_ScanQrInRegion, DefaultPipelineProfiles.QrReadFromRegionId,
            () => RunProfile(DefaultPipelineProfiles.QrReadFromRegionId)));
        // Wormholes entry under Tools — gated on the module flag. Opens the NewWormholeDialog
        // (§8.4) so the user picks Data vs Portal + title + (for Portal) the source folder.
        // The dialog hands the choice back to the manager which materialises the record.
        menu.Items.Add(tools);

        if (_modules.WormholesEnabled)
        {
            // Wormholes lives as a sibling of Tools at the top level — same vertical hierarchy
            // as Capture / Upload / Tools so the user reaches it in one click instead of
            // diving into Tools first. Subentries grow here (sort modes, show/hide all,
            // import/export, etc.) without polluting Tools' generic toolbox feel.
            var wormholes = new MenuItem { Header = Strings.Tray_Wormholes };
            wormholes.Items.Add(BuildMenuItem(Strings.Tray_NewWormhole,
                () => Run<AresToys.App.Services.Wormholes.IWormholeWindowManager>(m =>
                {
                    var dlg = new AresToys.App.Views.NewWormholeDialog();
                    var ok = dlg.ShowDialog() == true && dlg.Result is not null;
                    if (!ok) return;
                    var choice = dlg.Result!;
                    _ = m.CreateAsync(choice.Title, choice.SourceFolder, CancellationToken.None);
                })));
            // Recovery one-click when wormholes end up off-screen (multi-monitor rearrange,
            // monitor disconnect, weird DPI). Cascade-resets every live wormhole onto the
            // primary monitor and brings them to the front.
            wormholes.Items.Add(BuildMenuItem(Strings.Tray_RecenterWormholes,
                () => Run<AresToys.App.Services.Wormholes.IWormholeWindowManager>(m => m.RecenterAll())));
            wormholes.Items.Add(new Separator());
            // Toggle entries: each mirrors a built-in workflow (WormholesToggle*Id) so the
            // assigned hotkey shows up alongside the label via BuildShortcutMenuItem.
            // Clicking dispatches straight to the manager's ToggleAll*Async methods, identical
            // to what the workflow would do — same end state, no profile lookup needed.
            wormholes.Items.Add(BuildShortcutMenuItem(Strings.Tray_ToggleHideWormholes, DefaultPipelineProfiles.WormholesToggleHideId,
                () => Run<AresToys.App.Services.Wormholes.IWormholeWindowManager>(m => _ = m.ToggleAllHiddenAsync(CancellationToken.None))));
            wormholes.Items.Add(BuildShortcutMenuItem(Strings.Tray_ToggleLockWormholes, DefaultPipelineProfiles.WormholesToggleLockId,
                () => Run<AresToys.App.Services.Wormholes.IWormholeWindowManager>(m => _ = m.ToggleAllLockedAsync(CancellationToken.None))));
            wormholes.Items.Add(BuildShortcutMenuItem(Strings.Tray_ToggleCollapseWormholes, DefaultPipelineProfiles.WormholesToggleCollapseId,
                () => Run<AresToys.App.Services.Wormholes.IWormholeWindowManager>(m => _ = m.ToggleAllRolledAsync(CancellationToken.None))));
            wormholes.Items.Add(BuildShortcutMenuItem(Strings.Tray_ToggleTopmostWormholes, DefaultPipelineProfiles.WormholesToggleTopmostId,
                () => Run<AresToys.App.Services.Wormholes.IWormholeWindowManager>(m => _ = m.ToggleAllTopmostAsync(CancellationToken.None))));

            wormholes.Items.Add(new Separator());
            wormholes.Items.Add(BuildWormholeLayoutSubmenu());
            wormholes.Items.Add(BuildMenuItem(Strings.Tray_OpenPresetsFolder, OnOpenPresetsFolder));

            menu.Items.Add(wormholes);
        }

        menu.Items.Add(new Separator());
        // Clipboard + Launcher tray entries are gated on the module flags — when a module is
        // disabled in Settings → Modules we drop the entry entirely so the menu doesn't advertise
        // shortcuts that won't work (no ingestion = empty clipboard window, no pre-warm = grey
        // flash on launcher). The DI singletons stay registered: an unexpected code path that
        // resolves the window still gets a working instance, just without history / cells.
        if (_modules.ClipboardEnabled)
        {
            menu.Items.Add(BuildShortcutMenuItem(Strings.Tray_OpenClipboard, DefaultPipelineProfiles.ShowPopupId,
            () =>
            {
                // Same toggle / capture-foreground / show-and-activate sequence the
                // OpenClipboardWindowTask runs — replicated inline so the tray entry
                // doesn't need a PipelineContext.
                if (AresToys.App.Views.ClipboardWindow.IsOpen)
                {
                    AresToys.App.Views.ClipboardWindow.RequestClose();
                    return;
                }
                Run<TargetWindowTracker>(t => t.CaptureCurrentForeground());
                // Mirrors the launcher pattern below: PrepareAsync fires before Show so the
                // row list is ready when the window paints. Same fire-and-forget continuation
                // (Run<T> takes Action<T>, async-await needs an outer wrapper).
                _ = ShowClipboardAsync();
                async Task ShowClipboardAsync()
                {
                    try
                    {
                        var w = (AresToys.App.Views.ClipboardWindow)_services.GetService(typeof(AresToys.App.Views.ClipboardWindow))!;
                        try { await w.PrepareAsync(); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Clipboard PrepareAsync failed; showing anyway"); }
                        w.Show();
                        w.Activate();
                    }
                    catch (Exception ex) { _logger.LogWarning(ex, "Clipboard show failed"); }
                }
            }));
        }
        if (_modules.LauncherEnabled)
        {
            menu.Items.Add(BuildShortcutMenuItem(Strings.Tray_OpenLauncher, DefaultPipelineProfiles.OpenLauncherId,
            () =>
            {
                // Same toggle pattern as OpenLauncherMenuTask: closed → open + activate; open
                // → close. Inline because the tray menu doesn't carry a PipelineContext.
                if (AresToys.App.Views.LauncherWindow.IsOpen)
                {
                    AresToys.App.Views.LauncherWindow.RequestClose();
                    return;
                }
                // PrepareAsync must complete before Show so the cell grid is populated when
                // the window paints — fire-and-forget continuation chain on the UI dispatcher
                // (already on it; tray menu click runs on the message pump). Errors in
                // PrepareAsync surface via the dispatcher's unhandled-exception path; we
                // don't crash the menu over a bad SQLite read.
                _ = ShowLauncherAsync();
                async Task ShowLauncherAsync()
                {
                    try
                    {
                        var w = (AresToys.App.Views.LauncherWindow)_services.GetService(typeof(AresToys.App.Views.LauncherWindow))!;
                        try { await w.PrepareAsync(); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Launcher PrepareAsync failed; showing anyway"); }
                        w.Show();
                        w.Activate();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Launcher show failed");
                    }
                }
            }));
        }
        // Incognito is a clipboard-capture suppression toggle — meaningless when the clipboard
        // module is off (no ingestion to suppress). Gate on the same flag.
        if (_modules.ClipboardEnabled)
        {
            menu.Items.Add(BuildShortcutMenuItem(Strings.Tray_ToggleIncognito, DefaultPipelineProfiles.ToggleIncognitoId,
                () => Run<IncognitoModeService>(s => _ = s.ToggleAsync(CancellationToken.None))));
        }
        menu.Items.Add(new Separator());
        // Three extra entries before "Open screenshot folder" — deep-link into the matching
        // Settings tabs. Beside the user value (one-click into common tabs), they push the
        // bottom items lower on screen so the Capture submenu (which auto-flips upward when
        // the tray menu is anchored at the screen edge) has room to render adjacent to its
        // parent without the gap WPF-UI's shadow margin would otherwise reveal.
        menu.Items.Add(BuildMenuItem(Strings.Tray_AppTheme,
            () => OnOpenSettingsTab(AresToys.App.ViewModels.SettingsTab.Theme)));
        menu.Items.Add(BuildMenuItem(Strings.Tray_Hotkeys,
            () => OnOpenSettingsTab(AresToys.App.ViewModels.SettingsTab.Hotkeys)));
        menu.Items.Add(BuildMenuItem(Strings.Tray_CaptureSettings,
            () => OnOpenSettingsTab(AresToys.App.ViewModels.SettingsTab.Capture)));
        menu.Items.Add(BuildMenuItem(Strings.Tray_OpenScreenshotFolder, OnOpenScreenshotFolder));
        menu.Items.Add(BuildMenuItem(Strings.Tray_AppSettings,
            () => OnOpenSettingsTab(AresToys.App.ViewModels.SettingsTab.Settings)));
        // Mutually exclusive: when running unelevated show "Restart as administrator" (triggers
        // the same ShellExecute runas hop the Settings card uses); when running elevated show
        // "Restart normally" (Shell.Application.ShellExecute hand-off to Explorer for a medium-IL
        // child). One-shot bypass — neither entry touches the persistent "Always run as admin"
        // preference; that stays the exclusive job of the Settings checkbox.
        if (_elevation.IsProcessElevated)
        {
            menu.Items.Add(BuildMenuItem(Strings.Tray_RestartNormally, OnRestartNormally));
        }
        else
        {
            menu.Items.Add(BuildMenuItem(Strings.Tray_RestartAsAdmin, OnRestartAsAdmin));
        }
        menu.Items.Add(BuildMenuItem(Strings.Tray_Quit, OnQuit));
        return menu;
    }

    private void OnRestartAsAdmin()
    {
        _logger.LogInformation("Restart-as-admin requested from tray menu");
        if (ElevationService.RestartElevated())
        {
            Application.Current.Shutdown();
        }
        // UAC declined: stay in the current non-elevated session, no state to revert.
    }

    private void OnRestartNormally()
    {
        _logger.LogInformation("Restart-normally requested from tray menu");
        if (ElevationService.RestartUnelevated())
        {
            Application.Current.Shutdown();
        }
    }

    /// <summary>Tray-menu shortcut to the configured capture folder. Mirrors what
    /// <see cref="AresToys.App.Services.PipelineTasks.OpenScreenshotFolderTask"/> does — read
    /// <c>capture.folder</c>, expand env vars, ensure the folder exists, hand off to Explorer.
    /// Inlined here (rather than dispatching the pipeline task) because the tray menu has no
    /// PipelineContext and the operation is two lines. Uses fire-and-forget async so the menu
    /// click returns immediately while the settings read + Explorer launch run on the
    /// thread-pool — failures get logged, the user sees no popup.</summary>
    private async void OnOpenScreenshotFolder()
    {
        try
        {
            var template = await _settings.GetAsync("capture.folder", CancellationToken.None).ConfigureAwait(true)
                ?? "%USERPROFILE%\\Pictures\\AresToys";
            var folder = Environment.ExpandEnvironmentVariables(template);
            System.IO.Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folder}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tray: failed to open screenshot folder");
        }
    }

    private MenuItem BuildMonitorSubmenu()
    {
        // Built each time the parent menu opens so monitor changes (hot-plug, resolution change,
        // primary swap) are reflected without an app restart.
        var item = new MenuItem { Header = Strings.Tray_Monitor };
        item.SubmenuOpened += (_, _) =>
        {
            item.Items.Clear();
            var monitors = AresToys.Capture.MonitorEnumeration.Enumerate();
            if (monitors.Count == 0)
            {
                item.Items.Add(new MenuItem { Header = "(no monitors detected)", IsEnabled = false });
                return;
            }
            for (var i = 0; i < monitors.Count; i++)
            {
                var monitor = monitors[i];
                var label = $"{i + 1}. {monitor.Name}  {monitor.Width}×{monitor.Height}{(monitor.IsPrimary ? "  (primary)" : string.Empty)}";
                item.Items.Add(BuildMenuItem(label,
                    () => Run<CaptureCoordinator>(c => _ = c.CaptureMonitorAsync(monitor, CancellationToken.None))));
            }
        };
        // Placeholder so the chevron renders before the submenu is opened the first time.
        item.Items.Add(new MenuItem { Header = "(populating…)", IsEnabled = false });
        return item;
    }

    private void OnUploadFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Upload file with AresToys",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog() != true) return;
        var path = dialog.FileName;
        Run<ManualUploadService>(s => _ = s.UploadFileAsync(path, CancellationToken.None));
    }

    private void OnUploadFromClipboard()
        => Run<ManualUploadService>(s => _ = s.UploadCurrentClipboardAsync(CancellationToken.None));

    private void Run<T>(Action<T> action) where T : notnull
    {
        try
        {
            var service = (T)_services.GetService(typeof(T))!;
            action(service);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tray menu action threw for {Type}", typeof(T).Name);
        }
    }

    private void OnTrayBalloonTipClicked(object sender, RoutedEventArgs e)
    {
        var handler = _pendingToastClick;
        _pendingToastClick = null;
        if (handler is null) return;
        try { handler(); }
        catch (Exception ex) { _logger.LogError(ex, "Toast click handler threw"); }
    }

    private void OnOpen()
    {
        if (_mainWindow is null) return;
        if (!_mainWindow.IsVisible) _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    /// <summary>Open the main window and switch to a specific Settings tab in one go. The
    /// SettingsViewModel is the MainWindow's DataContext; flipping <see cref="AresToys.App.ViewModels.SettingsViewModel.SelectedTab"/>
    /// triggers the tab-strip + page-router bindings.</summary>
    private void OnOpenSettingsTab(AresToys.App.ViewModels.SettingsTab tab)
    {
        OnOpen();
        if (_mainWindow?.DataContext is AresToys.App.ViewModels.SettingsViewModel vm)
            vm.SelectedTab = tab;
    }

    private void OnQuit()
    {
        _logger.LogInformation("Quit requested from tray menu");
        Application.Current.Shutdown();
    }

    /// <summary>Variant of <see cref="BuildMenuItem"/> that fetches the current hotkey for
    /// <paramref name="profileId"/> and tacks it onto the label as the "\tShortcut" suffix.
    /// When the hotkey is unbound, only the bare label renders (no trailing whitespace). Lets
    /// menu items reflect a user-edited binding the next time the menu is built — combined
    /// with the Changed-handler in the ctor (rebuilds the menu on rebind), the suffix stays
    /// in sync without a restart.</summary>
    private MenuItem BuildShortcutMenuItem(string label, string profileId, Action onClick)
    {
        var shortcut = LookupShortcut(profileId);
        var header = string.IsNullOrEmpty(shortcut) ? label : $"{label}\t{shortcut}";
        return BuildMenuItem(header, onClick);
    }

    /// <summary>"Wormhole layout" submenu, populated lazily on open (like the monitor submenu) so
    /// it always reflects the current preset list + the preset auto-mapped to this monitor setup.
    /// Clicking a preset restores it; "Save current layout as…" prompts for a name; when this
    /// setup already maps to a preset an "Update ‹name›" entry re-snapshots it.</summary>
    private MenuItem BuildWormholeLayoutSubmenu()
    {
        var item = new MenuItem { Header = Strings.Tray_WormholeLayout };
        item.SubmenuOpened += (_, _) =>
        {
            item.Items.Clear();
            Run<AresToys.App.Services.Wormholes.IWormholeWindowManager>(m =>
            {
                var presets = m.ListPresetsAsync(CancellationToken.None).GetAwaiter().GetResult();
                var current = m.CurrentSetupPresetAsync(CancellationToken.None).GetAwaiter().GetResult();

                if (presets.Count == 0)
                {
                    item.Items.Add(new MenuItem { Header = Strings.Tray_NoPresets, IsEnabled = false });
                }
                else
                {
                    foreach (var name in presets)
                    {
                        var captured = name;
                        // Mark the preset auto-associated with the current setup with a dot.
                        // Bullet (U+25CF) marks the preset auto-associated with this setup.
                        // Escaped, not inline, to keep the source pure ASCII per project convention.
                        var header = string.Equals(captured, current, StringComparison.OrdinalIgnoreCase)
                            ? captured + "  " + char.ConvertFromUtf32(0x25CF)
                            : captured;
                        item.Items.Add(BuildMenuItem(header,
                            () => Run<AresToys.App.Services.Wormholes.IWormholeWindowManager>(
                                mm => _ = mm.RestorePresetAsync(captured, CancellationToken.None))));
                    }
                }

                item.Items.Add(new Separator());
                item.Items.Add(BuildMenuItem(Strings.Tray_SaveLayoutAs, PromptAndSaveWormholePreset));
                if (!string.IsNullOrEmpty(current))
                {
                    var cur = current;
                    item.Items.Add(BuildMenuItem(Strings.Tray_UpdateLayout.Replace("{0}", cur),
                        () => Run<AresToys.App.Services.Wormholes.IWormholeWindowManager>(
                            mm => _ = mm.SaveCurrentAsPresetAsync(cur, CancellationToken.None))));
                }
            });
        };
        // Placeholder so the chevron renders before the submenu is first opened.
        item.Items.Add(new MenuItem { Header = "…", IsEnabled = false });
        return item;
    }

    /// <summary>Prompt for a preset name and save the current wormhole layout under it. When the
    /// typed name matches an existing preset (case-insensitive) it would silently overwrite it,
    /// so confirm first — mirrors the Settings panel's "Save current layout as…" behaviour.</summary>
    private void PromptAndSaveWormholePreset()
    {
        var dlg = new AresToys.App.Views.TextPromptDialog(Strings.WormholePresets_SaveTitle, Strings.WormholePresets_NameLabel);
        if (dlg.ShowDialog() != true || string.IsNullOrEmpty(dlg.Result)) return;
        var name = dlg.Result.Trim();
        Run<AresToys.App.Services.Wormholes.IWormholeWindowManager>(m =>
        {
            var existing = m.ListPresetsAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (existing.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase)))
            {
                var confirm = MessageBox.Show(
                    Strings.WormholePresets_OverwriteConfirm.Replace("{0}", name),
                    "AresToys", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
                if (confirm != MessageBoxResult.OK) return;
            }
            _ = m.SaveCurrentAsPresetAsync(name, CancellationToken.None);
        });
    }

    /// <summary>Open the wormhole presets folder in Explorer. Creates it on demand (it may not
    /// exist before the first preset is saved) so the click never dead-ends. Mirrors
    /// <see cref="OnOpenScreenshotFolder"/>.</summary>
    private void OnOpenPresetsFolder()
    {
        Run<AresToys.App.Services.Wormholes.IWormholeWindowManager>(m =>
        {
            try
            {
                var folder = m.PresetsFolderPath;
                System.IO.Directory.CreateDirectory(folder);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{folder}\"",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tray: failed to open presets folder");
            }
        });
    }

    /// <summary>Synchronous lookup of the effective hotkey for a pipeline profile id. The
    /// underlying SQLite read is sub-millisecond, so blocking the menu-build dispatcher tick
    /// is fine. Returns the formatted "Ctrl + Alt + R"-style string, or empty when unbound /
    /// the lookup fails (the menu just renders without a shortcut suffix).</summary>
    private string LookupShortcut(string profileId)
    {
        try
        {
            var def = _hotkeys.GetEffectiveAsync(profileId, CancellationToken.None).GetAwaiter().GetResult();
            if (def.Modifiers == AresToys.Hotkeys.HotkeyModifiers.None && def.VirtualKey == 0) return string.Empty;
            return Hotkeys.HotkeyDisplay.Format(def.Modifiers, def.VirtualKey);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tray menu: failed to resolve shortcut for {Id}", profileId);
            return string.Empty;
        }
    }

    /// <summary>Build a tray menu entry. Headers can carry a "Label\tShortcut" form (legacy
    /// from the days when WPF rendered \t as a tab spacer); here we split on the tab and
    /// build a 2-column Grid header so the shortcut sits flush right and gets the accent
    /// foreground. Plain headers (no \t) keep using the simple string form for cheaper
    /// rendering.</summary>
    private static MenuItem BuildMenuItem(string header, Action onClick)
    {
        var item = new MenuItem();
        var tabIdx = header.IndexOf('\t');
        if (tabIdx < 0)
        {
            item.Header = header;
        }
        else
        {
            var label = header[..tabIdx];
            var shortcut = header[(tabIdx + 1)..];
            // Stretch the header presenter so the inner Grid can right-align the shortcut
            // against the menu's full width instead of just the label width.
            item.HorizontalContentAlignment = HorizontalAlignment.Stretch;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new TextBlock { Text = label });
            var shortcutTb = new TextBlock
            {
                Text = shortcut,
                Margin = new Thickness(24, 0, 0, 0),
                Opacity = 0.85,
            };
            // DynamicResource so the shortcut tracks the live accent (changes in Theme tab
            // re-tint instantly without rebuilding the menu).
            shortcutTb.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
                "AccentBackgroundLightBrush");
            Grid.SetColumn(shortcutTb, 1);
            grid.Children.Add(shortcutTb);
            item.Header = grid;
        }
        item.Click += (_, _) => onClick();
        return item;
    }

    public void ShowToast(string title, string message, Action? onClick = null)
    {
        _pendingToastClick = onClick;
        try
        {
            _icon.ShowNotification(title, message, H.NotifyIcon.Core.NotificationIcon.Info);
        }
        catch (Exception ex)
        {
            _pendingToastClick = null;
            _logger.LogWarning(ex, "Tray toast failed; capture pipeline continues");
        }
    }

    public void Dispose() => _icon.Dispose();

    private sealed class RelayCommand(Action execute) : ICommand
    {
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }
    }
}
