using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShareQ.App.Native;
using ShareQ.App.Services;
using ShareQ.App.Services.Launcher;
using ShareQ.App.Services.PipelineTasks;
using ShareQ.App.ViewModels;
using ShareQ.App.Views;
using ShareQ.Capture.DependencyInjection;
using ShareQ.Clipboard;
using ShareQ.Clipboard.DependencyInjection;
using ShareQ.Core.Pipeline;
using ShareQ.Editor.DependencyInjection;
using ShareQ.Hotkeys;
using ShareQ.Pipeline.DependencyInjection;
using ShareQ.Pipeline.Profiles;
using ShareQ.Plugins.DependencyInjection;
using ShareQ.Storage.Database;
using ShareQ.Storage.DependencyInjection;

namespace ShareQ.App;

// CA1001 doesn't apply: WPF Application is not IDisposable but has OnExit which we use for cleanup.
#pragma warning disable CA1001
public partial class App : Application
#pragma warning restore CA1001
{
    private IHost? _host;
    private KeyboardHook? _keyboardHook;

    public IServiceProvider Services => _host?.Services
        ?? throw new InvalidOperationException("Host not initialized.");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Pull the .sxcu / .sxie file path out of argv if present (Explorer file association
        // invokes us with the path as argv[0] / argv[1] depending on how shell picked it). We
        // only act on this once we know we're the primary; secondary instances forward it
        // through the pipe.
        var sxcuPath = e.Args.FirstOrDefault(a =>
            !string.IsNullOrEmpty(a) && a.EndsWith(".sxcu", StringComparison.OrdinalIgnoreCase));
        var sxiePath = e.Args.FirstOrDefault(a =>
            !string.IsNullOrEmpty(a) && a.EndsWith(".sxie", StringComparison.OrdinalIgnoreCase));

        // --upload <path>: Explorer context-menu entry uses this. We don't validate the file
        // exists here (the upload pipeline will surface its own error); we just thread the path
        // through to the primary instance or handle it ourselves below.
        string? uploadPath = null;
        for (var i = 0; i < e.Args.Length - 1; i++)
        {
            if (string.Equals(e.Args[i], "--upload", StringComparison.OrdinalIgnoreCase))
            {
                uploadPath = e.Args[i + 1];
                break;
            }
        }

        var guard = new SingleInstanceGuard();
        if (!guard.IsPrimary)
        {
            // Priority: upload (most specific) → sxcu → sxie → bare relaunch. Each prefix routes
            // the payload to the matching handler in the primary's AnotherInstanceStarted listener.
            string msg;
            if (uploadPath is not null)
                msg = SingleInstanceGuard.UploadPrefix + Path.GetFullPath(uploadPath);
            else if (sxcuPath is not null)
                msg = SingleInstanceGuard.SxcuPrefix + Path.GetFullPath(sxcuPath);
            else if (sxiePath is not null)
                msg = SingleInstanceGuard.SxiePrefix + Path.GetFullPath(sxiePath);
            else
                msg = SingleInstanceGuard.ShowMessage;
            try { await guard.NotifyExistingInstanceAsync(msg, CancellationToken.None); }
            catch { /* primary may have died */ }
            guard.Dispose();
            Shutdown();
            return;
        }

        // Single shared sink — both the InMemoryLoggerProvider (registered with the host's
        // logging builder below) and the DebugViewModel (registered in DI) refer to the same
        // instance, so log entries flow ILogger → DebugLogService.Entries → Debug tab UI.
        var debugLog = new ShareQ.App.Services.Logging.DebugLogService();

        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                // Minimum Debug across the board so the in-app Debug tab is actually useful —
                // by default Host.CreateDefaultBuilder filters at Information for app categories
                // and Warning for Microsoft.* / System.*, which would hide most LogDebug calls.
                logging.SetMinimumLevel(LogLevel.Debug);
                logging.AddConsole();
                logging.AddProvider(new ShareQ.App.Services.Logging.InMemoryLoggerProvider(debugLog));
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton(debugLog);
                services.AddSingleton(guard);

                services.AddShareQStorage();
                services.AddShareQClipboard();
                services.AddShareQCapture();
                services.AddShareQPipeline();
                services.AddShareQPlugins();
                services.AddShareQEditor();

                // Uploader registration mirrors ShareX: popular services ship as compiled
                // IUploader implementations in ShareQ.Uploaders (with config UI for keys when
                // needed), and the long tail is covered by user-supplied ShareX-compatible .sxcu
                // files under %LOCALAPPDATA%\ShareQ\custom-uploaders\.
                services.AddSingleton<ShareQ.PluginContracts.IPluginConfigStoreFactory>(sp =>
                    new ShareQ.App.Services.Plugins.HostPluginConfigStoreFactory(
                        sp.GetRequiredService<ShareQ.Storage.Settings.ISettingsStore>()));

                // Single OAuth flow service shared by every OAuth-capable uploader. Its HttpClient
                // only ever talks to provider token endpoints — short-lived, no streaming — so a
                // process-wide instance is fine (no socket exhaustion concerns at this volume).
                services.AddSingleton<ShareQ.Uploaders.OAuth.OAuthFlowService>(sp =>
                    new ShareQ.Uploaders.OAuth.OAuthFlowService(
                        new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
                        sp.GetService<ILogger<ShareQ.Uploaders.OAuth.OAuthFlowService>>()));

                // Bundled uploaders. One HttpClient per instance (5-min timeout, same shape as the
                // .sxcu engine) — keeps the registration trivial without an IHttpClientFactory.
                static HttpClient NewUploaderHttp() => new() { Timeout = TimeSpan.FromMinutes(5) };
                services.AddSingleton<ShareQ.PluginContracts.IUploader>(sp =>
                    new ShareQ.Uploaders.Catbox.CatboxUploader(NewUploaderHttp(),
                        sp.GetService<ILogger<ShareQ.Uploaders.Catbox.CatboxUploader>>()));
                services.AddSingleton<ShareQ.PluginContracts.IUploader>(sp =>
                    new ShareQ.Uploaders.UguuSe.UguuSeUploader(NewUploaderHttp(),
                        sp.GetService<ILogger<ShareQ.Uploaders.UguuSe.UguuSeUploader>>()));
                services.AddSingleton<ShareQ.PluginContracts.IUploader>(sp =>
                    new ShareQ.Uploaders.PasteRs.PasteRsUploader(NewUploaderHttp(),
                        sp.GetService<ILogger<ShareQ.Uploaders.PasteRs.PasteRsUploader>>()));

                // Shared folder uploader — no HTTP client, just file I/O. Per-uploader config
                // store carries the target folder + optional URL prefix from Settings UI.
                services.AddSingleton<ShareQ.PluginContracts.IUploader>(sp =>
                    new ShareQ.Uploaders.SharedFolder.SharedFolderUploader(
                        sp.GetRequiredService<ShareQ.PluginContracts.IPluginConfigStoreFactory>().Create("shared-folder"),
                        sp.GetService<ILogger<ShareQ.Uploaders.SharedFolder.SharedFolderUploader>>()));

                // URL shorteners — anonymous, no setup. Same operator (Memset) runs both is.gd
                // and v.gd; we ship both so the user has a fallback when one rate-limits.
                services.AddSingleton<ShareQ.PluginContracts.IUploader>(sp =>
                    new ShareQ.Uploaders.IsGd.IsGdUploader(NewUploaderHttp(),
                        sp.GetService<ILogger<ShareQ.Uploaders.IsGd.IsGdUploader>>()));
                services.AddSingleton<ShareQ.PluginContracts.IUploader>(sp =>
                    new ShareQ.Uploaders.Vgd.VgdUploader(NewUploaderHttp(),
                        sp.GetService<ILogger<ShareQ.Uploaders.Vgd.VgdUploader>>()));
                services.AddSingleton<ShareQ.PluginContracts.IUploader>(sp =>
                    new ShareQ.Uploaders.Bitly.BitlyUploader(NewUploaderHttp(),
                        sp.GetRequiredService<ShareQ.PluginContracts.IPluginConfigStoreFactory>().Create("bitly"),
                        sp.GetService<ILogger<ShareQ.Uploaders.Bitly.BitlyUploader>>()));

                // Keyed bundled uploaders. Each gets a per-id IPluginConfigStore so credentials
                // (Imgur Client ID, ImgBB / Pastebin API keys, Gist PAT) live under the
                // plugin.{id}.{key} namespace in ISettingsStore — sensitive values are DPAPI-
                // encrypted by the underlying store. Settings UI walks IConfigurableUploader to
                // render the form.
                services.AddSingleton<ShareQ.PluginContracts.IUploader>(sp =>
                    new ShareQ.Uploaders.Imgur.ImgurUploader(NewUploaderHttp(),
                        sp.GetService<ILogger<ShareQ.Uploaders.Imgur.ImgurUploader>>()));
                services.AddSingleton<ShareQ.PluginContracts.IUploader>(sp =>
                    new ShareQ.Uploaders.ImgBB.ImgBBUploader(NewUploaderHttp(),
                        sp.GetRequiredService<ShareQ.PluginContracts.IPluginConfigStoreFactory>().Create("imgbb"),
                        sp.GetService<ILogger<ShareQ.Uploaders.ImgBB.ImgBBUploader>>()));
                services.AddSingleton<ShareQ.PluginContracts.IUploader>(sp =>
                    new ShareQ.Uploaders.Pastebin.PastebinUploader(NewUploaderHttp(),
                        sp.GetRequiredService<ShareQ.PluginContracts.IPluginConfigStoreFactory>().Create("pastebin"),
                        sp.GetService<ILogger<ShareQ.Uploaders.Pastebin.PastebinUploader>>()));
                services.AddSingleton<ShareQ.PluginContracts.IUploader>(sp =>
                    new ShareQ.Uploaders.Gist.GistUploader(NewUploaderHttp(),
                        sp.GetRequiredService<ShareQ.PluginContracts.IPluginConfigStoreFactory>().Create("gist"),
                        sp.GetService<ILogger<ShareQ.Uploaders.Gist.GistUploader>>()));

                // OAuth-bundled uploaders. Each gets the shared OAuthFlowService for the sign-in
                // dance + its own per-id IPluginConfigStore for tokens and per-account preferences.
                services.AddSingleton<ShareQ.PluginContracts.IUploader>(sp =>
                    new ShareQ.Uploaders.OneDrive.OneDriveUploader(NewUploaderHttp(),
                        sp.GetRequiredService<ShareQ.PluginContracts.IPluginConfigStoreFactory>().Create("onedrive"),
                        sp.GetRequiredService<ShareQ.Uploaders.OAuth.OAuthFlowService>(),
                        sp.GetService<ILogger<ShareQ.Uploaders.OneDrive.OneDriveUploader>>()));
                services.AddSingleton<ShareQ.PluginContracts.IUploader>(sp =>
                    new ShareQ.Uploaders.GoogleDrive.GoogleDriveUploader(NewUploaderHttp(),
                        sp.GetRequiredService<ShareQ.PluginContracts.IPluginConfigStoreFactory>().Create("googledrive"),
                        sp.GetRequiredService<ShareQ.Uploaders.OAuth.OAuthFlowService>(),
                        sp.GetService<ILogger<ShareQ.Uploaders.GoogleDrive.GoogleDriveUploader>>()));
                services.AddSingleton<ShareQ.PluginContracts.IUploader>(sp =>
                    new ShareQ.Uploaders.Dropbox.DropboxUploader(NewUploaderHttp(),
                        sp.GetRequiredService<ShareQ.PluginContracts.IPluginConfigStoreFactory>().Create("dropbox"),
                        sp.GetRequiredService<ShareQ.Uploaders.OAuth.OAuthFlowService>(),
                        sp.GetService<ILogger<ShareQ.Uploaders.Dropbox.DropboxUploader>>()));

                // Long-tail .sxcu loader. EnsureDefaults() copies any bundled .sxcu defaults the
                // first time they're seen — currently empty since all our defaults are bundled
                // uploaders, but kept for future additions.
                ShareQ.App.Services.Plugins.CustomUploaderSeeding.EnsureDefaults();
                ShareQ.CustomUploaders.CustomUploaderRegistry.RegisterFromFolder(
                    ShareQ.CustomUploaders.CustomUploaderRegistry.DefaultFolder,
                    services,
                    onError: (file, ex) => System.Diagnostics.Debug.WriteLine($"[CustomUploaders] failed to load {file}: {ex.Message}"));

                services.AddSingleton<ShareQ.App.Services.Plugins.PluginRegistry>(sp =>
                    new ShareQ.App.Services.Plugins.PluginRegistry(
                        sp.GetRequiredService<ShareQ.Storage.Settings.ISettingsStore>(),
                        sp.GetServices<ShareQ.PluginContracts.IUploader>()));
                services.AddSingleton<ShareQ.Plugins.IUploaderResolver>(sp =>
                    sp.GetRequiredService<ShareQ.App.Services.Plugins.PluginRegistry>());

                // App-side pipeline tasks (registered as IPipelineTask alongside Pipeline's baked tasks).
                services.AddSingleton<IPipelineTask, CopyImageToClipboardTask>();
                services.AddSingleton<IPipelineTask, ApplyImageEffectsPresetTask>();
                services.AddSingleton<IPipelineTask, CopyTextToClipboardTask>();
                services.AddSingleton<IPipelineTask, UploadClipboardTextTask>();
                services.AddSingleton<IPipelineTask, NotifyToastTask>();
                services.AddSingleton<IPipelineTask, OpenEditorBeforeUploadTask>();
                services.AddSingleton<IPipelineTask, ToggleIncognitoTask>();
                services.AddSingleton<IPipelineTask, ColorSamplerTask>();
                services.AddSingleton<IPipelineTask, ColorPickerTask>();
                services.AddSingleton<IPipelineTask, CopyColorAsHexTask>();
                services.AddSingleton<IPipelineTask, CopyColorAsRgbTask>();
                services.AddSingleton<IPipelineTask, CopyColorAsRgbaTask>();
                services.AddSingleton<IPipelineTask, CopyColorAsHsbTask>();
                services.AddSingleton<IPipelineTask, CopyColorAsCmykTask>();
                services.AddSingleton<IPipelineTask, CopyColorAsDecimalTask>();
                services.AddSingleton<IPipelineTask, CopyColorAsLinearTask>();
                services.AddSingleton<IPipelineTask, CopyColorAsBgraTask>();
                // Periodic per-category retention sweep (30s tick) — see CategoryRotationScheduler.
                // The MaxItems cap is enforced inline on add inside ItemStore; this scheduler is
                // the time-based safety net for AutoCleanupAfter (minutes).
                services.AddHostedService<CategoryRotationScheduler>();

                services.AddSingleton<IPipelineTask, CaptureRegionTask>();
                services.AddSingleton<IPipelineTask, CaptureActiveWindowTask>();
                services.AddSingleton<IPipelineTask, CaptureActiveMonitorTask>();
                services.AddSingleton<IPipelineTask, CaptureWebpageTask>();
                services.AddSingleton<IPipelineTask, CaptureSelectedExplorerFileTask>();
                services.AddSingleton<IPipelineTask, QrReadTask>();
                services.AddSingleton<QrReaderService>();
                services.AddSingleton<WebpageCaptureService>();
                services.AddSingleton<IPipelineTask, RecordScreenTask>();
                services.AddSingleton<IPipelineTask, OpenScreenshotFolderTask>();
                services.AddSingleton<IPipelineTask, PasteHistoryItemTask>();
                services.AddSingleton<IPipelineTask, PressKeyTask>();
                services.AddSingleton<IPipelineTask, DelayTask>();
                services.AddSingleton<IPipelineTask, OpenUrlTask>();
                services.AddSingleton<IPipelineTask, ShowInExplorerTask>();
                services.AddSingleton<IPipelineTask, SaveAsTask>();
                services.AddSingleton<ShareQ.App.Services.Qr.QrCodeService>();
                services.AddSingleton<IPipelineTask, QrCodeTask>();
                services.AddSingleton<IPipelineTask, SaveQrCodeAsImageTask>();
                services.AddSingleton<IPipelineTask, SaveQrCodeAsSvgTask>();
                services.AddSingleton<IPipelineTask, CopyQrCodeToClipboardTask>();
                services.AddSingleton<IPipelineTask, PinToScreenTask>();
                services.AddSingleton<IPipelineTask, LaunchAppTask>();
                services.AddSingleton<IPipelineTask, OpenFileTask>();
                services.AddSingleton<IPipelineTask, RunCommandTask>();
                services.AddSingleton<IPipelineTask, OpenLauncherMenuTask>();
                services.AddSingleton<IPipelineTask, OpenLauncherDragModeTask>();
                services.AddSingleton<IPipelineTask, OpenClipboardWindowTask>();
                services.AddSingleton<IPipelineTask, OpenSettingsTask>();
                // Singleton + Hide (not Close) for snappy reopen on the global shortcut —
                // same lifetime pattern the launcher uses.
                services.AddSingleton<ShareQ.App.Views.ClipboardWindow>();
                services.AddSingleton<LauncherStore>();
                services.AddSingleton<IconService>();
                // Singleton + Hide() instead of Close() so the user gets an instant re-show
                // on the global shortcut (no visual-tree rebuild). State that needs to be
                // refreshed each time (cells, drag-mode flag, search) is reset in
                // IsVisibleChanged inside the window itself.
                services.AddSingleton<LauncherWindow>();

                services.AddSingleton<IncognitoModeService>();
                services.AddSingleton<ClipboardIngestionService>();
                services.AddSingleton<TargetWindowTracker>();
                services.AddSingleton<AutoPaster>();
                services.AddSingleton<CaptureCoordinator>();
                services.AddSingleton<ManualUploadService>();
                services.AddSingleton<IToastNotifier, WindowsToastNotifier>();
                services.AddSingleton<ShareQ.Core.Imaging.IImageEncoder, WpfImageEncoder>();
                services.AddSingleton<CaptureImageOutputService>();
                services.AddSingleton<WebView2AvailabilityService>();
                services.AddSingleton<ExternalTextEditorService>();
                // Velopack-backed self-update. Disabled at runtime (IsAvailable=false) when the
                // app isn't running from a Velopack-managed install — no harm, just shows the
                // "Check for updates" button as disabled in Settings.
                services.AddSingleton<ShareQ.Updater.UpdaterService>();
                services.AddSingleton<AutostartService>();
                services.AddSingleton<PinToScreenLauncher>();
                services.AddSingleton<EditorLauncher>();
                services.AddSingleton<ScreenColorPickerService>();
                services.AddSingleton<ColorWheelLauncher>();
                services.AddSingleton<Services.Recording.FfmpegLocator>();
                services.AddSingleton<Services.Recording.FfmpegDownloader>();
                services.AddSingleton<Services.Recording.ScreenRecordingService>();
                services.AddSingleton<Services.Recording.RecordingCoordinator>();
                services.AddSingleton<ShareQ.Editor.Persistence.ColorRecentsStore>();
                services.AddSingleton<ShareQ.Editor.Persistence.EditorDefaultsStore>();

                services.AddTransient<PopupWindowViewModel>();

                services.AddSingleton<ShareQ.App.Services.Hotkeys.HotkeyConfigService>();
                services.AddSingleton<WorkflowRunner>();
                services.AddSingleton<UploadersViewModel>();
                services.AddSingleton<HotkeysViewModel>();
                services.AddSingleton<WorkflowActionProvider>();
                services.AddSingleton<WorkflowEditorViewModel>();
                services.AddSingleton<WorkflowsViewModel>();
                services.AddSingleton<CaptureDefaultsViewModel>();
                services.AddSingleton<ThemeService>();
                services.AddSingleton<LocalizationService>();
                services.AddSingleton<SettingsBackupService>();
                services.AddSingleton<ThemeViewModel>();
                services.AddSingleton<CategoriesViewModel>();
                services.AddSingleton<DebugViewModel>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<MainWindow>();
                services.AddSingleton<TrayIconService>();
            })
            .Build();

        await _host.StartAsync();

        var db = _host.Services.GetRequiredService<IShareQDatabase>();
        await db.InitializeAsync(CancellationToken.None);

        // Apply the user's theme BEFORE any window resolves: ThemeService writes to App.Resources
        // and to WPF-UI's accent manager, both of which are read at control-template instantiation
        // time. Loading after MainWindow would cause a one-frame flash of the default blue accent.
        var theme = _host.Services.GetRequiredService<ThemeService>();
        await theme.LoadAsync(CancellationToken.None);

        // Pull the persisted UI language and apply it to the current thread + app default before
        // any window resolves — Strings.ResourceManager reads CurrentUICulture, so loading after
        // MainWindow would render the first frame in English even if the user picked Italian.
        // Attach FIRST: LocalizedStrings.Attach wires the singleton that every {Markup:Loc Key=…}
        // binding shares; LoadAsync below now raises CultureChanged on initial load too, so any
        // surface that happens to materialise its bindings between InitializeComponent and the
        // dispatched ApplyToThread call gets a free refresh once the persisted culture is live.
        var localization = _host.Services.GetRequiredService<LocalizationService>();
        ShareQ.App.Markup.LocalizedStrings.Instance.Attach(localization);
        await localization.LoadAsync(CancellationToken.None);

        // Seed default pipeline profiles (idempotent — leaves user customizations).
        var seeder = _host.Services.GetRequiredService<PipelineProfileSeeder>();
        await seeder.SeedAsync(CancellationToken.None);

        // Register the dynamic-options provider for the WorkflowActionCatalog ComboBox source
        // keyed by "image_effect_presets". The lookup is sync because the catalog is consumed
        // on the UI thread when each step row materialises; .GetAwaiter().GetResult() is safe
        // here — IImageEffectPresetStore.ListAsync uses the singleton SqliteConnection (no
        // SyncContext capture), so no deadlock risk.
        var presetStore = _host.Services.GetRequiredService<ShareQ.Storage.ImageEffects.IImageEffectPresetStore>();
        ShareQ.App.ViewModels.WorkflowActionCatalog.OptionsProviders["image_effect_presets"] = () =>
        {
            try
            {
                var presets = presetStore.ListAsync(System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                return presets.Select(p => p.Name).ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        };

        // Image-format dropdown for SaveToFile's "format" override. Empty entry = "use the
        // bag's current extension" (default — typically the global capture format set in
        // Settings → Capture). Explicit format = re-encode through IImageEncoder before write.
        ShareQ.App.ViewModels.WorkflowActionCatalog.OptionsProviders["image_formats"] = () =>
            new[] { string.Empty, "PNG", "JPEG", "BMP", "GIF" };

        var incognito = _host.Services.GetRequiredService<IncognitoModeService>();
        await incognito.LoadAsync(CancellationToken.None);
        var notifier = _host.Services.GetRequiredService<IToastNotifier>();
        incognito.StateChanged += (_, _) =>
            notifier.Show("Incognito mode", incognito.IsActive ? "ON — clipboard items won't be captured" : "OFF — capture resumed");

        var tray = _host.Services.GetRequiredService<TrayIconService>();
        var window = _host.Services.GetRequiredService<MainWindow>();
        tray.Attach(window);

        // Self-update: subscribe to UpdateAvailable for the toast, then fire one silent check on
        // startup. Click on the toast launches the same flow as the Settings button — a simple
        // confirm dialog that can either install + restart now or defer until next launch.
        // Wired AFTER tray creation so the closure-captured tray reference is non-null on first use.
        var updater = _host.Services.GetRequiredService<ShareQ.Updater.UpdaterService>();
        updater.UpdateAvailable += (_, args) =>
        {
            Dispatcher.Invoke(() =>
            {
                tray.ShowToast(
                    "ShareQ update available",
                    $"Version {args.Version} is ready. Click to install.",
                    onClick: () => _ = PromptInstallUpdateAsync(updater, args.Info));
            });
        };
        // Fire-and-forget — silent check, errors logged inside the service.
        _ = Task.Run(() => updater.CheckSilentlyAsync(CancellationToken.None));

        guard.AnotherInstanceStarted += (_, message) =>
        {
            Dispatcher.Invoke(() =>
            {
                // Always bring the main window forward — both for plain re-launches and for
                // .sxcu opens, since the import dialog is owned by the main window.
                if (!window.IsVisible) window.Show();
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Activate();

                if (message.StartsWith(SingleInstanceGuard.SxcuPrefix, StringComparison.Ordinal))
                {
                    var path = message[SingleInstanceGuard.SxcuPrefix.Length..];
                    HandleSxcuOpen(path, window);
                }
                else if (message.StartsWith(SingleInstanceGuard.SxiePrefix, StringComparison.Ordinal))
                {
                    var path = message[SingleInstanceGuard.SxiePrefix.Length..];
                    HandleSxieOpen(path, window);
                }
                else if (message.StartsWith(SingleInstanceGuard.UploadPrefix, StringComparison.Ordinal))
                {
                    var path = message[SingleInstanceGuard.UploadPrefix.Length..];
                    HandleUploadOpen(path);
                }
            });
        };

        // If the primary process itself was launched WITH a .sxcu path (first-ever invocation
        // happens to be the file-association handler), surface the import dialog after the main
        // window is up so the dialog has an Owner to centre on.
        if (sxcuPath is not null)
        {
            // Fire-and-forget: dispatched at Loaded priority so it runs after the main window
            // is rendered and can serve as the dialog's Owner. We don't need the operation handle.
            _ = Dispatcher.BeginInvoke(new Action(() => HandleSxcuOpen(Path.GetFullPath(sxcuPath), window)),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
        // Same cold-start handling for .sxie — opens the image-effects window with the imported
        // preset, mirroring how ShareX itself handles a double-click on a .sxie file.
        if (sxiePath is not null)
        {
            _ = Dispatcher.BeginInvoke(new Action(() => HandleSxieOpen(Path.GetFullPath(sxiePath), window)),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
        // Same idea for --upload: cold-start from the Explorer context-menu — the user clicked
        // "Upload with ShareQ" and ShareQ wasn't running yet. Process the file then sit in the
        // tray; popping the Settings window unsolicited would feel wrong for a one-shot upload.
        if (uploadPath is not null)
        {
            _ = Dispatcher.BeginInvoke(new Action(() => HandleUploadOpen(Path.GetFullPath(uploadPath))),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
        guard.StartListening();

        // Cold-start from the context menu (--upload) is silent: tray-only, no Settings popup.
        // Sxcu / sxie still want the main window because their import dialogs need an Owner.
        var startSilent = uploadPath is not null && sxcuPath is null && sxiePath is null;
        if (!startSilent) window.Show();
        window.Closing += (sender, args) =>
        {
            args.Cancel = true;
            ((Window)sender!).Hide();
        };

        var helper = new WindowInteropHelper(window);
        helper.EnsureHandle();
        var source = HwndSource.FromHwnd(helper.Handle)!;
        source.AddHook(WndProc);

        var hotkeyConfig = _host.Services.GetRequiredService<ShareQ.App.Services.Hotkeys.HotkeyConfigService>();
        var hotkeyLogger = _host.Services.GetRequiredService<ILogger<App>>();

        // Single low-level keyboard hook for ALL global hotkeys (popup, region capture, color picker,
        // recording, ...). Hook-based registration wins against any foreground app's keyboard
        // shortcut and even against OS-reserved combos like Win+Shift+S — same approach PowerToys
        // KeyboardManager uses. In-app shortcuts (popup Ctrl+P / Enter / Ctrl+1-9, editor bindings)
        // stay focus-local via the WPF Window's KeyDown handlers — those don't pass through here.
        _keyboardHook = new KeyboardHook();

        // Every hotkey now invokes a workflow (pipeline profile) by id. The 1:1 mapping between
        // hotkey id and workflow id is intentional: HotkeyConfigService's Catalog is built from
        // profiles, so the ids are workflow ids. WorkflowRunner loads the profile and runs its steps.
        var workflowRunner = _host.Services.GetRequiredService<WorkflowRunner>();
        Action MakeCallback(string workflowId)
            => () => Dispatcher.InvokeAsync(() => _ = workflowRunner.RunAsync(workflowId, CancellationToken.None));

        async Task RegisterCatalogAsync(string id)
        {
            var def = await hotkeyConfig.GetEffectiveAsync(id, CancellationToken.None);
            if (def.VirtualKey == 0)
            {
                hotkeyLogger.LogInformation("Hotkey {Id} unbound — skipping registration", id);
                return;
            }
            _keyboardHook.Register(id, def.Modifiers, def.VirtualKey, MakeCallback(id), suppress: true);
            hotkeyLogger.LogInformation("Hotkey {Id} bound to {Combo}",
                id, ShareQ.App.Services.Hotkeys.HotkeyDisplay.Format(def.Modifiers, def.VirtualKey));
        }

        var catalog = await hotkeyConfig.GetCatalogAsync(CancellationToken.None);
        foreach (var entry in catalog)
            await RegisterCatalogAsync(entry.Id);

        // Settings UI raises Changed when the user rebinds, clears, or resets → keep the hook in
        // sync. VK 0 means the user cleared the binding, so unregister and don't re-register.
        hotkeyConfig.Changed += (_, def) =>
        {
            _keyboardHook.Unregister(def.Id);
            if (def.VirtualKey == 0)
            {
                hotkeyLogger.LogInformation("Hotkey {Id} cleared", def.Id);
                return;
            }
            _keyboardHook.Register(def.Id, def.Modifiers, def.VirtualKey, MakeCallback(def.Id), suppress: true);
            hotkeyLogger.LogInformation("Hotkey {Id} re-bound to {Combo}",
                def.Id, ShareQ.App.Services.Hotkeys.HotkeyDisplay.Format(def.Modifiers, def.VirtualKey));
        };

        try { _keyboardHook.Install(); hotkeyLogger.LogInformation("Low-level keyboard hook installed for global hotkeys."); }
        catch (Exception ex) { hotkeyLogger.LogWarning(ex, "Failed to install keyboard hook"); }

        var ingestion = _host.Services.GetRequiredService<ClipboardIngestionService>();
        ingestion.Start(helper.Handle);
    }


    /// <summary>Show the import-confirmation dialog for a .sxcu file the user double-clicked
    /// (or that came in via Explorer file association). On confirm we copy the file into the
    /// custom-uploaders folder and tell the user to restart — adding an uploader at runtime
    /// would mean re-rebuilding the DI graph, which isn't worth the complexity for a one-time
    /// import flow.</summary>
    private void HandleSxcuOpen(string path, Window owner)
    {
        try
        {
            if (!File.Exists(path))
            {
                MessageBox.Show(owner, $"File not found:\n{path}", "ShareQ",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var json = File.ReadAllText(path);
            var config = ShareQ.CustomUploaders.CustomUploaderConfigLoader.Parse(json);
            if (config is null || !ShareQ.CustomUploaders.CustomUploaderConfigLoader.IsValid(config))
            {
                MessageBox.Show(owner,
                    $"This file isn't a valid .sxcu — it's missing the required Name or RequestURL fields.\n\n{path}",
                    "ShareQ", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var dlg = new ShareQ.App.Views.SxcuImportDialog(path, config) { Owner = owner };
            if (dlg.ShowDialog() == true && dlg.InstalledPath is not null)
            {
                MessageBox.Show(owner,
                    $"Installed to:\n{dlg.InstalledPath}\n\nRestart ShareQ to load the new uploader.",
                    "ShareQ", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"Couldn't open .sxcu file:\n{ex.Message}",
                "ShareQ", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Direct import of a .sxie image-effects preset (Explorer file association).
    /// Mirrors ShareX's behaviour — no confirmation dialog, just open the image-effects window
    /// with the imported preset selected. If an editor window is already open, we reuse it
    /// (importing into its existing VM + bringing it forward) instead of stacking N windows.</summary>
    private async void HandleSxieOpen(string path, Window owner)
    {
        try
        {
            if (!File.Exists(path))
            {
                MessageBox.Show(owner, $"File not found:\n{path}", "ShareQ",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Reuse an open editor if there is one — Application.Windows includes any
            // ImageEffectsWindow opened from Settings or from a previous .sxie association.
            var existing = Application.Current.Windows
                .OfType<ShareQ.App.Views.ImageEffectsWindow>()
                .FirstOrDefault();
            if (existing is not null)
            {
                if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
                existing.Activate();
                await existing.ImportSxieAsync(path).ConfigureAwait(true);
                return;
            }

            // No editor open yet — spin one up with the same DI wiring as the
            // "Open image effects editor…" button in Settings (SQLite preset store + persisted
            // window placement).
            var presetStore = _host!.Services.GetRequiredService<ShareQ.Storage.ImageEffects.IImageEffectPresetStore>();
            var settingsStore = _host.Services.GetRequiredService<ShareQ.Storage.Settings.ISettingsStore>();
            var vm = new ShareQ.App.ViewModels.ImageEffects.ImageEffectsViewModel(
                ShareQ.ImageEffects.ImageEffectRegistry.Default, presetStore);
            var window = new ShareQ.App.Views.ImageEffectsWindow(vm, settingsStore) { Owner = owner };
            window.Show();
            await vm.ImportSxieFileAsync(path).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"Couldn't open .sxie file:\n{ex.Message}",
                "ShareQ", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Route a file path from the Explorer "Upload with ShareQ" verb through the
    /// pipeline profile chosen by the user in Settings (default <c>manual-upload</c>). Lets the
    /// context-menu entry trigger any user-visible workflow — e.g. "save to local folder", "upload
    /// to Imgur and copy link", or a custom-built profile. Fire-and-forget; the workflow's own
    /// toast/clipboard steps handle user feedback.</summary>
    private void HandleUploadOpen(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                MessageBox.Show($"File not found:\n{path}", "ShareQ",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var manual = _host?.Services.GetService(typeof(ManualUploadService)) as ManualUploadService;
            if (manual is null)
            {
                MessageBox.Show("ShareQ isn't fully initialised yet — try again in a moment.",
                    "ShareQ", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var settings = _host?.Services.GetService(typeof(ShareQ.Storage.Settings.ISettingsStore)) as ShareQ.Storage.Settings.ISettingsStore;
            _ = Task.Run(async () =>
            {
                // Resolve the profile id off the UI thread (settings reads hit SQLite). Empty / null
                // → default profile, same fallback ManualUploadService applies for unknown ids.
                var profileId = settings is null
                    ? ShareQ.Pipeline.Profiles.DefaultPipelineProfiles.ManualUploadId
                    : (await settings.GetAsync(ExplorerContextMenuWorkflowKey, CancellationToken.None).ConfigureAwait(false))
                        is { Length: > 0 } stored
                            ? stored
                            : ShareQ.Pipeline.Profiles.DefaultPipelineProfiles.ManualUploadId;
                await manual.UploadFileToProfileAsync(path, profileId, CancellationToken.None).ConfigureAwait(false);
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't start upload:\n{ex.Message}",
                "ShareQ", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Settings key carrying the pipeline profile id triggered by the Explorer
    /// "Upload with ShareQ" verb. Empty / unset → <c>manual-upload</c>. Kept here so the App
    /// handler and the Settings UI stay in sync.</summary>
    public const string ExplorerContextMenuWorkflowKey = "explorer.context_menu.workflow";

    /// <summary>Show a confirm dialog for an available update. OK → download + apply + restart.
    /// Cancel → leave it for the next launch (Velopack will offer it again on the next silent
    /// check). Used by both the toast click and the Settings → "Check for updates" button.</summary>
    internal static async Task PromptInstallUpdateAsync(ShareQ.Updater.UpdaterService updater, Velopack.UpdateInfo info)
    {
        var version = info.TargetFullRelease.Version.ToString();
        var choice = MessageBox.Show(
            $"ShareQ {version} is available.\n\nDownload and restart now?",
            "Update ShareQ",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information,
            MessageBoxResult.OK);
        if (choice != MessageBoxResult.OK) return;
        try { await updater.DownloadAndRestartAsync(info, CancellationToken.None).ConfigureAwait(true); }
        catch (Exception ex)
        {
            MessageBox.Show($"Update failed:\n{ex.Message}", "ShareQ",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WM_HOTKEY no longer needed (all hotkeys go through the low-level keyboard hook). Only the
        // clipboard-update message is still routed through here for the Win32ClipboardListener.
        if (msg == AppNativeMethods.WmClipboardUpdate)
        {
            var listener = Services.GetService<IClipboardListener>();
            handled = listener?.OnWindowMessage(msg) ?? false;
            return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    /// <summary>True once <see cref="OnExit(ExitEventArgs)"/> has fired. Singleton windows
    /// (ClipboardWindow / LauncherWindow) intercept their Closing event to Hide instead of
    /// Close so the next hotkey press can re-Show them; during shutdown we need them to
    /// actually close, so they read this flag to skip the cancel-and-hide path.</summary>
    public static bool IsShuttingDown { get; private set; }

    protected override async void OnExit(ExitEventArgs e)
    {
        IsShuttingDown = true;
        _keyboardHook?.Dispose();
        if (_host is not null)
        {
            _host.Services.GetService<ClipboardIngestionService>()?.Dispose();
            _host.Services.GetService<IClipboardListener>()?.Dispose();
            _host.Services.GetService<TrayIconService>()?.Dispose();
            _host.Services.GetService<SingleInstanceGuard>()?.Dispose();
            _host.Services.GetService<ExternalTextEditorService>()?.Dispose();
            await _host.StopAsync(TimeSpan.FromSeconds(2));
            _host.Dispose();
        }
        base.OnExit(e);
    }
}
