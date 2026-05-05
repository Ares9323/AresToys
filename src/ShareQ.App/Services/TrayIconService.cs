using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ShareQ.Core.Pipeline;
using ShareQ.Pipeline;
using ShareQ.Pipeline.Profiles;
using ShareQ.Storage.Settings;
// Alias so we can keep `MenuItem` short while using WPF-UI's Fluent-styled subclass instead
// of System.Windows.Controls.MenuItem. The default WPF MenuItem template is rendered by the
// Aero/Aero2 theme dictionaries, which use internal brush keys we can't override from
// outside — even after setting ContextMenuBackground / SystemColors.MenuBrushKey at App
// level the submenu popup chrome stays on the system dark default. WPF-UI's MenuItem uses
// its own Fluent template that resolves through ContextMenuBackground / Surface* keys we
// already pin in ThemeService, so the menu themes correctly without any per-control work.
using MenuItem = Wpf.Ui.Controls.MenuItem;

namespace ShareQ.App.Services;

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
        ILogger<TrayIconService> logger)
    {
        _services = services;
        _settings = settings;
        _profiles = profiles;
        _executor = executor;
        _logger = logger;
        _icon = new TaskbarIcon
        {
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/icon.ico", UriKind.Absolute)),
            ToolTipText = "ShareQ",
            Visibility = Visibility.Visible
        };

        _icon.ContextMenu = BuildMenu();
        // Click routing reads from settings on every event so changes apply without restart.
        // Defaults map to the historical behaviour: left=open Settings, double=toggle popup, middle=nothing.
        _icon.LeftClickCommand = new RelayCommand(() => DispatchClick(LeftClickKey, DefaultPipelineProfiles.OpenSettingsId));
        _icon.DoubleClickCommand = new RelayCommand(() => DispatchClick(DoubleClickKey, DefaultPipelineProfiles.ShowPopupId));
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

    public void Attach(MainWindow window) => _mainWindow = window;

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        // Capture submenu — mirrors ShareX's top-level Capture menu.
        var capture = new MenuItem { Header = "Capture" };
        capture.Items.Add(BuildMenuItem("Fullscreen",
            () => Run<CaptureCoordinator>(c => _ = c.CaptureFullscreenAsync(CancellationToken.None))));
        capture.Items.Add(BuildMonitorSubmenu());
        capture.Items.Add(BuildMenuItem("Active window",
            () => Run<CaptureCoordinator>(c => _ = c.CaptureActiveWindowAsync(CancellationToken.None))));
        capture.Items.Add(BuildMenuItem("Region\tCtrl+Alt+R",
            () => Run<CaptureCoordinator>(c => _ = c.CaptureRegionAsync(CancellationToken.None))));
        capture.Items.Add(BuildMenuItem("Last region",
            () => Run<CaptureCoordinator>(c => _ = c.CaptureLastRegionAsync(CancellationToken.None))));
        // Webpage capture needs WebView2 Runtime. When missing (old Win10 builds without the
        // bundled runtime, stripped LTSC images), swap the action for "(install runtime)" that
        // opens the Microsoft download page rather than firing a capture that would just abort.
        var webView2 = _services.GetService(typeof(WebView2AvailabilityService)) as WebView2AvailabilityService;
        if (webView2?.IsAvailable ?? false)
        {
            capture.Items.Add(BuildMenuItem("Webpage…",
                () => Run<CaptureCoordinator>(c => _ = c.CaptureWebpageAsync(CancellationToken.None))));
        }
        else
        {
            capture.Items.Add(BuildMenuItem("Webpage… (install WebView2 Runtime)",
                () => webView2?.OpenInstallerPage()));
        }
        capture.Items.Add(new Separator());
        capture.Items.Add(BuildMenuItem("Screen recording\tCtrl+Alt+S",
            () => Run<Services.Recording.RecordingCoordinator>(c => _ = c.ToggleAsync(ShareQ.Capture.Recording.RecordingFormat.Mp4, CancellationToken.None))));
        capture.Items.Add(BuildMenuItem("Screen recording (GIF)\tCtrl+Alt+G",
            () => Run<Services.Recording.RecordingCoordinator>(c => _ = c.ToggleAsync(ShareQ.Capture.Recording.RecordingFormat.Gif, CancellationToken.None))));
        capture.Items.Add(new Separator());
        capture.Items.Add(BuildMenuItem("Color sampler\tCtrl+Shift+P",
            () => Run<ScreenColorPickerService>(s => s.PickAtCursor())));
        capture.Items.Add(BuildMenuItem("Color picker…",
            () => Run<ColorWheelLauncher>(l => _ = l.ShowAsync())));
        menu.Items.Add(capture);

        // Upload submenu — kicks off the manual-upload pipeline from arbitrary sources.
        var upload = new MenuItem { Header = "Upload" };
        upload.Items.Add(BuildMenuItem("Upload file…", OnUploadFile));
        upload.Items.Add(BuildMenuItem("Upload from clipboard", OnUploadFromClipboard));
        // Upload text/URL go in a later sprint (need a text-input dialog).
        menu.Items.Add(upload);

        // Tools submenu — placeholder; we'll grow this with QR / hash / ruler / etc.
        var tools = new MenuItem { Header = "Tools" };
        tools.Items.Add(BuildMenuItem("Color sampler",
            () => Run<ScreenColorPickerService>(s => s.PickAtCursor())));
        tools.Items.Add(BuildMenuItem("Color picker…",
            () => Run<ColorWheelLauncher>(l => _ = l.ShowAsync())));
        tools.Items.Add(BuildMenuItem("Pin to screen…",
            () => Run<PinToScreenLauncher>(p => _ = p.ShowAsync(CancellationToken.None))));
        tools.Items.Add(BuildMenuItem("QR generator…",
            () => Run<ShareQ.App.Services.Qr.QrCodeService>(qr =>
                  Run<ShareQ.Storage.Settings.ISettingsStore>(settings =>
                  Run<ShareQ.App.Services.ManualUploadService>(ingestion =>
            {
                var win = new ShareQ.App.Views.QrGeneratorWindow(qr, null, settings, ingestion);
                win.Show();
                win.Activate();
            })))));
        menu.Items.Add(tools);

        menu.Items.Add(new Separator());
        menu.Items.Add(BuildMenuItem("Open clipboard\tWin+V",
            () =>
            {
                // Same toggle / capture-foreground / show-and-activate sequence the
                // OpenClipboardWindowTask runs — replicated inline so the tray entry
                // doesn't need a PipelineContext.
                if (ShareQ.App.Views.ClipboardWindow.IsOpen)
                {
                    ShareQ.App.Views.ClipboardWindow.RequestClose();
                    return;
                }
                Run<TargetWindowTracker>(t => t.CaptureCurrentForeground());
                Run<ShareQ.App.Views.ClipboardWindow>(w => { w.Show(); w.Activate(); });
            }));
        menu.Items.Add(BuildMenuItem("Open launcher\tWin+Z",
            () =>
            {
                // Same toggle pattern as OpenLauncherMenuTask: closed → open + activate; open
                // → close. Inline because the tray menu doesn't carry a PipelineContext.
                if (ShareQ.App.Views.LauncherWindow.IsOpen)
                {
                    ShareQ.App.Views.LauncherWindow.RequestClose();
                    return;
                }
                Run<ShareQ.App.Views.LauncherWindow>(w => { w.Show(); w.Activate(); });
            }));
        menu.Items.Add(BuildMenuItem("Toggle incognito\tCtrl+Alt+I",
            () => Run<IncognitoModeService>(s => _ = s.ToggleAsync(CancellationToken.None))));
        menu.Items.Add(new Separator());
        menu.Items.Add(BuildMenuItem("Open screenshot folder", OnOpenScreenshotFolder));
        menu.Items.Add(BuildMenuItem("Settings…", OnOpen));
        menu.Items.Add(BuildMenuItem("Quit", OnQuit));
        return menu;
    }

    /// <summary>Tray-menu shortcut to the configured capture folder. Mirrors what
    /// <see cref="ShareQ.App.Services.PipelineTasks.OpenScreenshotFolderTask"/> does — read
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
                ?? "%USERPROFILE%\\Pictures\\ShareQ";
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
        var item = new MenuItem { Header = "Monitor" };
        item.SubmenuOpened += (_, _) =>
        {
            item.Items.Clear();
            var monitors = ShareQ.Capture.MonitorEnumeration.Enumerate();
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
            Title = "Upload file with ShareQ",
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

    private void OnQuit()
    {
        _logger.LogInformation("Quit requested from tray menu");
        Application.Current.Shutdown();
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
