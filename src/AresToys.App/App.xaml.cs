using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AresToys.App.Native;
using AresToys.App.Services;
using AresToys.App.Services.Launcher;
using AresToys.App.Services.PipelineTasks;
using AresToys.App.ViewModels;
using AresToys.App.Views;
using AresToys.Capture.DependencyInjection;
using AresToys.Clipboard;
using AresToys.Clipboard.DependencyInjection;
using AresToys.Core.Pipeline;
using AresToys.Editor.DependencyInjection;
using AresToys.Hotkeys;
using AresToys.Pipeline.DependencyInjection;
using AresToys.Pipeline.Profiles;
using AresToys.Plugins.DependencyInjection;
using AresToys.Storage.Database;
using AresToys.Storage.DependencyInjection;

namespace AresToys.App;

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

        // Wire app-wide TextBox numeric-nudge handlers once. EventManager class-level hooks
        // beat the implicit-Style route (which broke WPF-UI ui:TextBox rendering) — every
        // TextBox / ui:TextBox in the app picks up the wheel + arrow ±1 (±5 with Shift)
        // behaviour without touching individual XAML or stylesheets.
        AresToys.App.Behaviors.NumericInput.RegisterClassHandlers();

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

        // --create-wormhole <folder>: folder right-click verb. Same forward-or-handle dance as
        // --upload; the primary spawns a wormhole record with the folder as source + the folder
        // name as title, no dialog.
        string? createWormholePath = null;
        for (var i = 0; i < e.Args.Length - 1; i++)
        {
            if (string.Equals(e.Args[i], "--create-wormhole", StringComparison.OrdinalIgnoreCase))
            {
                createWormholePath = e.Args[i + 1];
                break;
            }
        }

        // --new-wormhole-dialog: background-area right-click verb (empty space in a folder /
        // desktop). Opens NewWormholeDialog so the user picks a folder; the folder being
        // viewed at click time is rarely what they want to mirror (e.g. desktop background →
        // shouldn't auto-create a Desktop-mirroring wormhole).
        var newWormholeDialogRequested = e.Args.Any(a =>
            string.Equals(a, "--new-wormhole-dialog", StringComparison.OrdinalIgnoreCase));

        var guard = new SingleInstanceGuard();
        if (!guard.IsPrimary)
        {
            // Priority: upload (most specific) → sxcu → sxie → bare relaunch. Each prefix routes
            // the payload to the matching handler in the primary's AnotherInstanceStarted listener.
            string msg;
            if (uploadPath is not null)
                msg = SingleInstanceGuard.UploadPrefix + Path.GetFullPath(uploadPath);
            else if (createWormholePath is not null)
                msg = SingleInstanceGuard.CreateWormholePrefix + Path.GetFullPath(createWormholePath);
            else if (newWormholeDialogRequested)
                msg = SingleInstanceGuard.NewWormholeDialogMessage;
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
        var debugLog = new AresToys.App.Services.Logging.DebugLogService();

        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                // Minimum Debug across the board so the in-app Debug tab is actually useful —
                // by default Host.CreateDefaultBuilder filters at Information for app categories
                // and Warning for Microsoft.* / System.*, which would hide most LogDebug calls.
                logging.SetMinimumLevel(LogLevel.Debug);
                logging.AddConsole();
                logging.AddProvider(new AresToys.App.Services.Logging.InMemoryLoggerProvider(debugLog));
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton(debugLog);
                services.AddSingleton(guard);

                services.AddAresToysStorage();
                services.AddAresToysClipboard();
                services.AddAresToysCapture();
                services.AddAresToysPipeline();
                services.AddAresToysPlugins();
                services.AddAresToysEditor();

                // Uploader registration mirrors ShareX: popular services ship as compiled
                // IUploader implementations in AresToys.Uploaders (with config UI for keys when
                // needed), and the long tail is covered by user-supplied ShareX-compatible .sxcu
                // files under %LOCALAPPDATA%\AresToys\custom-uploaders\.
                services.AddSingleton<AresToys.PluginContracts.IPluginConfigStoreFactory>(sp =>
                    new AresToys.App.Services.Plugins.HostPluginConfigStoreFactory(
                        sp.GetRequiredService<AresToys.Storage.Settings.ISettingsStore>()));

                // Single OAuth flow service shared by every OAuth-capable uploader. Its HttpClient
                // only ever talks to provider token endpoints — short-lived, no streaming — so a
                // process-wide instance is fine (no socket exhaustion concerns at this volume).
                services.AddSingleton<AresToys.Uploaders.OAuth.OAuthFlowService>(sp =>
                    new AresToys.Uploaders.OAuth.OAuthFlowService(
                        new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
                        sp.GetService<ILogger<AresToys.Uploaders.OAuth.OAuthFlowService>>()));

                // Bundled uploaders. One HttpClient per instance (5-min timeout, same shape as the
                // .sxcu engine) — keeps the registration trivial without an IHttpClientFactory.
                static HttpClient NewUploaderHttp() => new() { Timeout = TimeSpan.FromMinutes(5) };
                services.AddSingleton<AresToys.PluginContracts.IUploader>(sp =>
                    new AresToys.Uploaders.Catbox.CatboxUploader(NewUploaderHttp(),
                        sp.GetService<ILogger<AresToys.Uploaders.Catbox.CatboxUploader>>()));
                services.AddSingleton<AresToys.PluginContracts.IUploader>(sp =>
                    new AresToys.Uploaders.UguuSe.UguuSeUploader(NewUploaderHttp(),
                        sp.GetService<ILogger<AresToys.Uploaders.UguuSe.UguuSeUploader>>()));
                services.AddSingleton<AresToys.PluginContracts.IUploader>(sp =>
                    new AresToys.Uploaders.PasteRs.PasteRsUploader(NewUploaderHttp(),
                        sp.GetService<ILogger<AresToys.Uploaders.PasteRs.PasteRsUploader>>()));

                // Shared folder uploader — no HTTP client, just file I/O. Per-uploader config
                // store carries the target folder + optional URL prefix from Settings UI.
                services.AddSingleton<AresToys.PluginContracts.IUploader>(sp =>
                    new AresToys.Uploaders.SharedFolder.SharedFolderUploader(
                        sp.GetRequiredService<AresToys.PluginContracts.IPluginConfigStoreFactory>().Create("shared-folder"),
                        sp.GetService<ILogger<AresToys.Uploaders.SharedFolder.SharedFolderUploader>>()));

                // URL shorteners — anonymous, no setup. Same operator (Memset) runs both is.gd
                // and v.gd; we ship both so the user has a fallback when one rate-limits.
                services.AddSingleton<AresToys.PluginContracts.IUploader>(sp =>
                    new AresToys.Uploaders.IsGd.IsGdUploader(NewUploaderHttp(),
                        sp.GetService<ILogger<AresToys.Uploaders.IsGd.IsGdUploader>>()));
                services.AddSingleton<AresToys.PluginContracts.IUploader>(sp =>
                    new AresToys.Uploaders.Vgd.VgdUploader(NewUploaderHttp(),
                        sp.GetService<ILogger<AresToys.Uploaders.Vgd.VgdUploader>>()));
                services.AddSingleton<AresToys.PluginContracts.IUploader>(sp =>
                    new AresToys.Uploaders.Bitly.BitlyUploader(NewUploaderHttp(),
                        sp.GetRequiredService<AresToys.PluginContracts.IPluginConfigStoreFactory>().Create("bitly"),
                        sp.GetService<ILogger<AresToys.Uploaders.Bitly.BitlyUploader>>()));

                // Keyed bundled uploaders. Each gets a per-id IPluginConfigStore so credentials
                // (Imgur Client ID, ImgBB / Pastebin API keys, Gist PAT) live under the
                // plugin.{id}.{key} namespace in ISettingsStore — sensitive values are DPAPI-
                // encrypted by the underlying store. Settings UI walks IConfigurableUploader to
                // render the form.
                services.AddSingleton<AresToys.PluginContracts.IUploader>(sp =>
                    new AresToys.Uploaders.Imgur.ImgurUploader(NewUploaderHttp(),
                        sp.GetService<ILogger<AresToys.Uploaders.Imgur.ImgurUploader>>()));
                services.AddSingleton<AresToys.PluginContracts.IUploader>(sp =>
                    new AresToys.Uploaders.ImgBB.ImgBBUploader(NewUploaderHttp(),
                        sp.GetRequiredService<AresToys.PluginContracts.IPluginConfigStoreFactory>().Create("imgbb"),
                        sp.GetService<ILogger<AresToys.Uploaders.ImgBB.ImgBBUploader>>()));
                services.AddSingleton<AresToys.PluginContracts.IUploader>(sp =>
                    new AresToys.Uploaders.Pastebin.PastebinUploader(NewUploaderHttp(),
                        sp.GetRequiredService<AresToys.PluginContracts.IPluginConfigStoreFactory>().Create("pastebin"),
                        sp.GetService<ILogger<AresToys.Uploaders.Pastebin.PastebinUploader>>()));
                services.AddSingleton<AresToys.PluginContracts.IUploader>(sp =>
                    new AresToys.Uploaders.Gist.GistUploader(NewUploaderHttp(),
                        sp.GetRequiredService<AresToys.PluginContracts.IPluginConfigStoreFactory>().Create("gist"),
                        sp.GetService<ILogger<AresToys.Uploaders.Gist.GistUploader>>()));

                // OAuth-bundled uploaders. Each gets the shared OAuthFlowService for the sign-in
                // dance + its own per-id IPluginConfigStore for tokens and per-account preferences.
                services.AddSingleton<AresToys.PluginContracts.IUploader>(sp =>
                    new AresToys.Uploaders.OneDrive.OneDriveUploader(NewUploaderHttp(),
                        sp.GetRequiredService<AresToys.PluginContracts.IPluginConfigStoreFactory>().Create("onedrive"),
                        sp.GetRequiredService<AresToys.Uploaders.OAuth.OAuthFlowService>(),
                        sp.GetService<ILogger<AresToys.Uploaders.OneDrive.OneDriveUploader>>()));
                services.AddSingleton<AresToys.PluginContracts.IUploader>(sp =>
                    new AresToys.Uploaders.GoogleDrive.GoogleDriveUploader(NewUploaderHttp(),
                        sp.GetRequiredService<AresToys.PluginContracts.IPluginConfigStoreFactory>().Create("googledrive"),
                        sp.GetRequiredService<AresToys.Uploaders.OAuth.OAuthFlowService>(),
                        sp.GetService<ILogger<AresToys.Uploaders.GoogleDrive.GoogleDriveUploader>>()));
                services.AddSingleton<AresToys.PluginContracts.IUploader>(sp =>
                    new AresToys.Uploaders.Dropbox.DropboxUploader(NewUploaderHttp(),
                        sp.GetRequiredService<AresToys.PluginContracts.IPluginConfigStoreFactory>().Create("dropbox"),
                        sp.GetRequiredService<AresToys.Uploaders.OAuth.OAuthFlowService>(),
                        sp.GetService<ILogger<AresToys.Uploaders.Dropbox.DropboxUploader>>()));

                // Long-tail .sxcu loader. EnsureDefaults() copies any bundled .sxcu defaults the
                // first time they're seen — currently empty since all our defaults are bundled
                // uploaders, but kept for future additions.
                AresToys.App.Services.Plugins.CustomUploaderSeeding.EnsureDefaults();
                AresToys.CustomUploaders.CustomUploaderRegistry.RegisterFromFolder(
                    AresToys.CustomUploaders.CustomUploaderRegistry.DefaultFolder,
                    services,
                    onError: (file, ex) => System.Diagnostics.Debug.WriteLine($"[CustomUploaders] failed to load {file}: {ex.Message}"));

                services.AddSingleton<AresToys.App.Services.Plugins.PluginRegistry>(sp =>
                    new AresToys.App.Services.Plugins.PluginRegistry(
                        sp.GetRequiredService<AresToys.Storage.Settings.ISettingsStore>(),
                        sp.GetServices<AresToys.PluginContracts.IUploader>()));
                services.AddSingleton<AresToys.Plugins.IUploaderResolver>(sp =>
                    sp.GetRequiredService<AresToys.App.Services.Plugins.PluginRegistry>());

                // App-side pipeline tasks (registered as IPipelineTask alongside Pipeline's baked tasks).
                // Unified "Add * to AresToys clipboard" family (replaces the old Copy* / SaveQr*
                // tasks): each writes to the AresToys history and optionally pushes to the
                // Windows clipboard via the `alsoCopyToWindows` toggle.
                services.AddSingleton<IPipelineTask, AddFileToClipboardTask>();
                services.AddSingleton<IPipelineTask, AddImageToClipboardTask>();
                services.AddSingleton<IPipelineTask, AddTextToClipboardTask>();
                services.AddSingleton<IPipelineTask, TextToQrPngTask>();
                services.AddSingleton<IPipelineTask, TextToQrSvgTask>();
                services.AddSingleton<IPipelineTask, ApplyImageEffectsPresetTask>();
                services.AddSingleton<IPipelineTask, UploadClipboardTextTask>();
                services.AddSingleton<AresToys.App.Services.Notifications.ToastBuilderService>();
                services.AddSingleton<AresToys.Core.Pipeline.IPipelineNotifier>(
                    sp => sp.GetRequiredService<AresToys.App.Services.Notifications.ToastBuilderService>());
                services.AddSingleton<IPipelineTask, TraceToSvgTask>();
                services.AddSingleton<IPipelineTask, SaveSvgTask>();
                services.AddSingleton<IPipelineTask, RemoveBackgroundTask>();
                services.AddSingleton<IPipelineTask, OpenEditorBeforeUploadTask>();
                services.AddSingleton<IPipelineTask, ToggleIncognitoTask>();
                services.AddSingleton<IPipelineTask, ColorSamplerTask>();
                services.AddSingleton<IPipelineTask, ColorPickerTask>();
                services.AddSingleton<IPipelineTask, ConvertColorTask>();
                // Legacy CopyColorAs* — kept registered for backward compat with profiles that
                // were authored before 0.1.17 collapsed them into ConvertColor + AddText. Removed
                // from the workflow editor's "+ Add step" picker (no catalog entry) but the
                // runtime classes still resolve so an old saved step keeps producing clipboard
                // text instead of hard-failing on a missing TaskId.
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
                services.AddSingleton<IPipelineTask, WormholeBatchOpTask>();
                services.AddSingleton<IPipelineTask, WormholeCreateTask>();
                services.AddSingleton<IPipelineTask, CaptureActiveWindowTask>();
                services.AddSingleton<IPipelineTask, CaptureActiveMonitorTask>();
                services.AddSingleton<IPipelineTask, CaptureWebpageTask>();
                services.AddSingleton<IPipelineTask, CaptureSelectedExplorerFileTask>();
                services.AddSingleton<IPipelineTask, QrReadTask>();
                services.AddSingleton<QrReaderService>();
                services.AddSingleton<WebpageCaptureService>();
                services.AddSingleton<IPipelineTask, RecordScreenTask>();
                services.AddSingleton<IPipelineTask, SaveVideoFileTask>();
                services.AddSingleton<IPipelineTask, OpenScreenshotFolderTask>();
                services.AddSingleton<IPipelineTask, PasteHistoryItemTask>();
                services.AddSingleton<IPipelineTask, PressKeyTask>();
                services.AddSingleton<IPipelineTask, RepeatTask>();
                services.AddSingleton<IPipelineTask, EndRepeatTask>();
                services.AddSingleton<AresToys.Core.Pipeline.ICancelHotkeyRegistry,
                                      Services.Pipeline.CancelHotkeyRegistry>();
                services.AddSingleton<IPipelineTask, DelayTask>();
                services.AddSingleton<IPipelineTask, OpenUrlTask>();
                services.AddSingleton<IPipelineTask, ShowInExplorerTask>();
                services.AddSingleton<IPipelineTask, SaveAsTask>();
                services.AddSingleton<AresToys.App.Services.Qr.QrCodeService>();
                services.AddSingleton<IPipelineTask, QrCodeTask>();
                services.AddSingleton<IPipelineTask, PinToScreenTask>();
                services.AddSingleton<IPipelineTask, LaunchAppTask>();
                services.AddSingleton<IPipelineTask, OpenFileTask>();
                services.AddSingleton<IPipelineTask, RunCommandTask>();
                services.AddSingleton<IPipelineTask, OpenLauncherMenuTask>();
                services.AddSingleton<IPipelineTask, OpenLauncherDragModeTask>();
                services.AddSingleton<IPipelineTask, OpenClipboardWindowTask>();
                services.AddSingleton<IPipelineTask, OpenSettingsTask>();
                // Headless launcher firing (no overlay) + Nth-clipboard-entry paste — both
                // surface a toast on empty target so a missed binding doesn't fail silently.
                services.AddSingleton<AresToys.App.Services.Launcher.LauncherActionService>();
                services.AddSingleton<IPipelineTask, LauncherTriggerKeyTask>();
                services.AddSingleton<IPipelineTask, PasteClipboardEntryTask>();
                // Singleton + Hide (not Close) for snappy reopen on the global shortcut —
                // same lifetime pattern the launcher uses.
                services.AddSingleton<AresToys.App.Views.ClipboardWindow>();
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
                services.AddSingleton<AresToys.Pipeline.Tasks.IItemClipboardPublisher, WpfItemClipboardPublisher>();
                services.AddSingleton<CaptureCoordinator>();
                services.AddSingleton<ManualUploadService>();
                services.AddSingleton<IToastNotifier, WindowsToastNotifier>();
                services.AddSingleton<AresToys.Core.Imaging.IImageEncoder, WpfImageEncoder>();
                services.AddSingleton<CaptureImageOutputService>();
                services.AddSingleton<WebView2AvailabilityService>();
                services.AddSingleton<ExternalTextEditorService>();
                // Velopack-backed self-update. Disabled at runtime (IsAvailable=false) when the
                // app isn't running from a Velopack-managed install — no harm, just shows the
                // "Check for updates" button as disabled in Settings.
                services.AddSingleton<AresToys.Updater.UpdaterService>();
                services.AddSingleton<AutostartService>();
                services.AddSingleton<PinToScreenLauncher>();
                services.AddSingleton<EditorLauncher>();
                services.AddSingleton<ScreenColorPickerService>();
                services.AddSingleton<ColorWheelLauncher>();
                services.AddSingleton<Services.Recording.FfmpegLocator>();
                services.AddSingleton<Services.Recording.FfmpegDownloader>();
                services.AddSingleton<Services.Recording.ScreenRecordingService>();
                services.AddSingleton<Services.Recording.RecordingCoordinator>();
                services.AddSingleton<Services.Recording.VideoThumbnailService>();
                services.AddSingleton<AresToys.Editor.Persistence.ColorRecentsStore>();
                services.AddSingleton<AresToys.Editor.Persistence.EditorDefaultsStore>();
                services.AddSingleton<AresToys.AI.IImageTracer, AresToys.AI.PotraceImageTracer>();
                services.AddSingleton<TracePresetStore>();
                // Background removal: U2NetP ONNX model. Singleton because the ONNX session
                // costs ~150 ms to spin up + holds ~5 MB of model data + DirectML resources;
                // amortising it across the process lifetime keeps the first-use latency on
                // subsequent calls in the inference-only band (~100-500 ms).
                services.AddSingleton<AresToys.AI.IBackgroundRemover, AresToys.AI.U2NetBackgroundRemover>();

                // Singleton so SettingsViewModel + ClipboardWindow share the same instance —
                // toggling "Show snippet under label" in App Settings must push into the same
                // VM whose Rows the visible clipboard window is bound to. Disposal: the VM
                // subscribes only to other singletons (item / category stores), so the missed
                // Dispose at process shutdown is a no-op rather than a leak.
                services.AddSingleton<PopupWindowViewModel>();

                services.AddSingleton<AresToys.App.Services.Hotkeys.HotkeyConfigService>();
                services.AddSingleton<WorkflowRunner>();

                // Key Sequences module — see docs/superpowers/specs/2026-05-16-key-sequences-design.md
                // The KeyboardHook is also pulled from DI (was a private field; promoted so the
                // tracker can reuse the same instance the legacy hotkey registration uses).
                services.AddSingleton<AresToys.Hotkeys.KeyboardHook>();
                services.AddSingleton<AresToys.App.Services.KeySequences.KeySequenceModuleSettings>();
                services.AddSingleton<AresToys.App.Services.KeySequences.SequenceMatcher>();
                services.AddSingleton<AresToys.App.Services.KeySequences.ClipboardSequenceProvider>();
                services.AddSingleton<AresToys.App.Services.KeySequences.WorkflowSequenceProvider>();
                services.AddSingleton<AresToys.App.Services.KeySequences.ISequenceBindingProvider>(
                    sp => sp.GetRequiredService<AresToys.App.Services.KeySequences.ClipboardSequenceProvider>());
                services.AddSingleton<AresToys.App.Services.KeySequences.ISequenceBindingProvider>(
                    sp => sp.GetRequiredService<AresToys.App.Services.KeySequences.WorkflowSequenceProvider>());
                services.AddSingleton<AresToys.App.Services.KeySequences.SequenceBindingStore>();
                services.AddSingleton<AresToys.App.Services.KeySequences.SequenceOverlayHost>();
                services.AddSingleton<AresToys.App.Services.KeySequences.ISequenceOverlay>(
                    sp => sp.GetRequiredService<AresToys.App.Services.KeySequences.SequenceOverlayHost>());
                services.AddSingleton<AresToys.App.Services.KeySequences.SequenceDispatcher>();
                services.AddSingleton<AresToys.App.Services.KeySequences.KeySequenceTracker>();
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
                services.AddSingleton<WormholesViewModel>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<MainWindow>();
                services.AddSingleton<TrayIconService>();
                // Holds the runtime-resolved enabled state for the optional feature modules
                // (Clipboard, Launcher, Wormholes). Populated below from the persisted settings
                // keys *after* the host is built, before any eager init runs.
                services.AddSingleton<ModuleSettings>();

                // Wormholes (M-Wormholes-A skeleton — see docs/WormholesSpec.md). Registered
                // unconditionally so the services resolve from anywhere; the eager init path
                // below gates the actual window spawning on modules.WormholesEnabled.
                services.AddSingleton<AresToys.App.Services.Wormholes.IWormholeStore,
                                      AresToys.App.Services.Wormholes.WormholeStoreJson>();
                services.AddSingleton<AresToys.App.Services.Wormholes.DesktopLayerHost>();
                services.AddSingleton<AresToys.App.Services.Wormholes.WormholeDefaultsService>();
                services.AddSingleton<AresToys.App.Services.Wormholes.IWormholeWindowManager,
                                      AresToys.App.Services.Wormholes.WormholeWindowManager>();
            })
            .Build();

        await _host.StartAsync();

        var db = _host.Services.GetRequiredService<IAresToysDatabase>();
        await db.InitializeAsync(CancellationToken.None);

        // Hydrate module-enabled flags from the settings store. Done once here so every gate
        // point downstream (eager pre-warm, ClipboardIngestionService start, hotkey loop, tray
        // menu builder) reads a stable snapshot of "is this module on for this process". Toggling
        // in the Settings UI persists to the same keys but requires restart to take effect —
        // tearing down a live ingestion thread or hotkey hook mid-process isn't worth the
        // complexity for a feature most users will set once.
        var settingsStore = _host.Services.GetRequiredService<AresToys.Storage.Settings.ISettingsStore>();
        var modules = _host.Services.GetRequiredService<ModuleSettings>();
        var clipboardModuleRaw = await settingsStore.GetAsync(ModuleSettings.ClipboardKey, CancellationToken.None).ConfigureAwait(true);
        var launcherModuleRaw  = await settingsStore.GetAsync(ModuleSettings.LauncherKey,  CancellationToken.None).ConfigureAwait(true);
        var wormholesModuleRaw = await settingsStore.GetAsync(ModuleSettings.WormholesKey, CancellationToken.None).ConfigureAwait(true);
        var keySequencesModuleRaw = await settingsStore.GetAsync(ModuleSettings.KeySequencesKey, CancellationToken.None).ConfigureAwait(true);
        // Clipboard + Launcher default ON when unset (preserves historical behaviour for existing
        // installs). Wormholes + KeySequences default OFF — features still maturing, opt-in only.
        modules.ClipboardEnabled = !string.Equals(clipboardModuleRaw, "false", StringComparison.OrdinalIgnoreCase);
        modules.LauncherEnabled  = !string.Equals(launcherModuleRaw,  "false", StringComparison.OrdinalIgnoreCase);
        modules.WormholesEnabled =  string.Equals(wormholesModuleRaw, "true",  StringComparison.OrdinalIgnoreCase);
        modules.KeySequencesEnabled = string.Equals(keySequencesModuleRaw, "true", StringComparison.OrdinalIgnoreCase);

        // Wormholes manager init: hydrates records from wormholes.json and spawns a window per
        // non-hidden record. Skipped entirely when the module is off — the JSON file is never
        // even touched so the disk + memory footprint of disabled wormholes is zero. Run on a
        // Loaded-priority dispatcher continuation so the main window is up first; the wormhole
        // windows can take their time hydrating in the background.
        if (modules.WormholesEnabled)
        {
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, async () =>
            {
                try
                {
                    // Hydrate the global wormhole defaults BEFORE the manager spawns windows —
                    // otherwise the first frame of every wormhole renders at the built-in
                    // fallback (95% opacity) and snaps to the persisted value a tick later.
                    var defaults = _host!.Services.GetRequiredService<AresToys.App.Services.Wormholes.WormholeDefaultsService>();
                    await defaults.LoadAsync(CancellationToken.None).ConfigureAwait(true);

                    var manager = _host!.Services.GetRequiredService<AresToys.App.Services.Wormholes.IWormholeWindowManager>();
                    await manager.InitializeAsync(CancellationToken.None);

                    // Register the Explorer right-click "Create Wormhole" verb (idempotent, HKCU
                    // — no admin). Done after the manager init so the verb only appears when
                    // wormholes are actually working. The toggle-off side lives in the Modules
                    // tab; when the user disables wormholes there we should unregister too —
                    // wired through Settings UI separately.
                    try { AresToys.App.Services.Wormholes.WormholeShellRegistration.Register(); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"WormholeShellRegistration.Register failed: {ex}"); }
                }
                catch (Exception ex)
                {
                    _host!.Services.GetService<ILogger<App>>()?.LogWarning(ex, "Wormholes init failed");
                }
            });
        }

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
        AresToys.App.Markup.LocalizedStrings.Instance.Attach(localization);
        await localization.LoadAsync(CancellationToken.None);

        // Seed default pipeline profiles (idempotent — leaves user customizations).
        var seeder = _host.Services.GetRequiredService<PipelineProfileSeeder>();
        await seeder.SeedAsync(CancellationToken.None);

        // Register the dynamic-options provider for the WorkflowActionCatalog ComboBox source
        // keyed by "image_effect_presets". The lookup is sync because the catalog is consumed
        // on the UI thread when each step row materialises; .GetAwaiter().GetResult() is safe
        // here — IImageEffectPresetStore.ListAsync uses the singleton SqliteConnection (no
        // SyncContext capture), so no deadlock risk.
        var presetStore = _host.Services.GetRequiredService<AresToys.Storage.ImageEffects.IImageEffectPresetStore>();
        AresToys.App.ViewModels.WorkflowActionCatalog.OptionsProviders["image_effect_presets"] = () =>
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
        AresToys.App.ViewModels.WorkflowActionCatalog.OptionsProviders["image_formats"] = () =>
            new[] { string.Empty, "PNG", "JPEG", "BMP", "GIF" };

        // Video-format dropdown for SaveVideoFile's "format". mp4 is the fast path (the
        // recorder always produces mp4, so picking mp4 is a verbatim file write); gif / webm /
        // mov route through ffmpeg for a transcode + container swap.
        AresToys.App.ViewModels.WorkflowActionCatalog.OptionsProviders["video_formats"] = () =>
            new[] { "mp4", "gif", "webm", "mov" };

        // Colour-format dropdown for ConvertColor's "format". 4 hex variants (with/without #,
        // with/without alpha) are exposed as discrete options instead of conditional bool
        // params — keeps the editor row simple and avoids the "hide/show param" complexity
        // that a single Hex entry + alpha/hash toggles would need.
        AresToys.App.ViewModels.WorkflowActionCatalog.OptionsProviders["color_formats"] = () =>
            new[]
            {
                "hex", "hex-hash", "hex-alpha", "hex-hash-alpha",
                "rgb", "rgba", "hsb", "cmyk", "decimal", "linear", "bgra",
            };

        // Settings-tab dropdown for OpenSettings's "tab" parameter. Raw values match the
        // SettingsTab enum lowercased so OpenSettingsTask can Enum.TryParse them straight into
        // SettingsViewModel.SelectedTab. Display labels live in Services.SettingsTabLabels and
        // mirror the sidebar entries the user already recognises.
        AresToys.App.ViewModels.WorkflowActionCatalog.OptionsProviders["settings_tabs"] = () =>
            new[] { "settings", "hotkeys", "uploaders", "capture", "categories", "wormholes", "theme", "debug", "about" };

        // Trace-preset dropdown for Trace to SVG's "preset" config. Concatenates the stock
        // presets (TracePresets.Stock — verbatim Illustrator Image Trace defaults) with any
        // user-saved custom presets from the standalone trace window. Lookup at task runtime
        // is case-insensitive against the same two sources, so a saved preset name resolves
        // identically to its stock counterpart if names collide (custom wins on rebuild order).
        var tracePresetStore = _host.Services.GetRequiredService<AresToys.App.Services.TracePresetStore>();
        AresToys.App.ViewModels.WorkflowActionCatalog.OptionsProviders["trace_presets"] = () =>
        {
            var names = new List<string>(AresToys.AI.TracePresets.Stock.Count + 8);
            foreach (var p in AresToys.AI.TracePresets.Stock) names.Add(p.Name);
            try
            {
                var custom = tracePresetStore.GetAllAsync(System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                foreach (var p in custom) names.Add(p.Name);
            }
            catch { /* settings-store hiccup → just stock list */ }
            return names;
        };

        // Default-tool dropdown for "Open editor". Empty entry = "use last-used" (whatever the
        // user last left selected in EditorDefaultsStore); explicit value preselects that tool
        // on open. List = the EditorTool enum minus the Select pointer (not a drawing tool —
        // confusingly close to the empty "use last" entry) and Image (one-shot file picker).
        // Launcher tab + key dropdowns for the Trigger-launcher-key workflow task. Tabs are
        // the 10 numeric user tabs ("1".."9","0") — the F-strip isn't a "tab", function keys
        // live in their own namespace and the runtime ignores the tab field when the key is
        // F1..F10. Keys: F-row + the 30 QWERTY/punctuation cells from LauncherKeyboardLayout.
        AresToys.App.ViewModels.WorkflowActionCatalog.OptionsProviders["launcher_tabs"] = () =>
            AresToys.App.Services.Launcher.LauncherKeyboardLayout.TabKeys.ToArray();
        AresToys.App.ViewModels.WorkflowActionCatalog.OptionsProviders["launcher_keys"] = () =>
        {
            var list = new List<string>(AresToys.App.Services.Launcher.LauncherKeyboardLayout.FunctionKeys.Count + 30);
            list.AddRange(AresToys.App.Services.Launcher.LauncherKeyboardLayout.FunctionKeys);
            list.AddRange(AresToys.App.Services.Launcher.LauncherKeyboardLayout.AllTabKeyChars());
            return list;
        };

        // Clipboard categories — fed from the live category store so renames / additions show
        // up the next time the user opens the Workflow editor without app restart. The list is
        // re-evaluated at the moment the row materialises (see OptionsProviders contract).
        var categoriesStore = _host.Services.GetRequiredService<AresToys.Storage.Items.ICategoryStore>();
        AresToys.App.ViewModels.WorkflowActionCatalog.OptionsProviders["clipboard_categories"] = () =>
        {
            try
            {
                var cats = categoriesStore.ListAsync(CancellationToken.None).GetAwaiter().GetResult();
                // Empty string at the top = "all categories" — paste from the global "All"
                // view, matching the popup's All tab semantics.
                var list = new List<string> { string.Empty };
                foreach (var c in cats) list.Add(c.Name);
                return list;
            }
            catch
            {
                return new[] { string.Empty };
            }
        };

        // Uploader / shortener dropdowns for the consolidated Upload + Shorten URL workflow
        // descriptors. Lists are re-evaluated each time the workflow editor materialises a
        // step, so enabling / disabling plugins in Settings reflects immediately. Empty string
        // is the first entry of each list — the UploadTask auto-picks a destination from the
        // bag's file extension when the user leaves the field on empty.
        var pluginRegistry = _host.Services.GetRequiredService<AresToys.App.Services.Plugins.PluginRegistry>();
        AresToys.App.ViewModels.WorkflowActionCatalog.OptionsProviders["uploader_ids"] = () =>
        {
            var list = new List<string> { string.Empty };
            list.AddRange(pluginRegistry.AllUploaders
                .Where(u => (u.Capabilities & ~AresToys.PluginContracts.UploaderCapabilities.Url) != 0)
                .Select(u => u.Id)
                .OrderBy(id => id, StringComparer.Ordinal));
            return list;
        };
        AresToys.App.ViewModels.WorkflowActionCatalog.OptionsProviders["shortener_ids"] = () =>
        {
            var list = new List<string> { string.Empty };
            list.AddRange(pluginRegistry.AllUploaders
                .Where(u => (u.Capabilities & AresToys.PluginContracts.UploaderCapabilities.Url) == AresToys.PluginContracts.UploaderCapabilities.Url)
                .Select(u => u.Id)
                .OrderBy(id => id, StringComparer.Ordinal));
            return list;
        };

        AresToys.App.ViewModels.WorkflowActionCatalog.OptionsProviders["editor_tools"] = () =>
            new[]
            {
                string.Empty,
                nameof(AresToys.Editor.Tools.EditorTool.Crop),
                nameof(AresToys.Editor.Tools.EditorTool.Rectangle),
                nameof(AresToys.Editor.Tools.EditorTool.Ellipse),
                nameof(AresToys.Editor.Tools.EditorTool.Line),
                nameof(AresToys.Editor.Tools.EditorTool.Arrow),
                nameof(AresToys.Editor.Tools.EditorTool.Freehand),
                nameof(AresToys.Editor.Tools.EditorTool.Text),
                nameof(AresToys.Editor.Tools.EditorTool.StepCounter),
                nameof(AresToys.Editor.Tools.EditorTool.Blur),
                nameof(AresToys.Editor.Tools.EditorTool.Pixelate),
                nameof(AresToys.Editor.Tools.EditorTool.Spotlight),
                nameof(AresToys.Editor.Tools.EditorTool.SmartEraser),
            };

        var incognito = _host.Services.GetRequiredService<IncognitoModeService>();
        await incognito.LoadAsync(CancellationToken.None);
        var notifier = _host.Services.GetRequiredService<IToastNotifier>();
        incognito.StateChanged += (_, _) =>
            notifier.Show("Incognito mode", incognito.IsActive ? "ON — clipboard items won't be captured" : "OFF — capture resumed");

        var tray = _host.Services.GetRequiredService<TrayIconService>();
        var window = _host.Services.GetRequiredService<MainWindow>();
        tray.Attach(window);

        // Self-update: subscribe to UpdateAvailable for the toast, then fire one silent check on
        // startup IF the user hasn't opted out via Settings → "Look for updates at startup".
        // Click on the toast launches the same flow as the Settings button — a simple confirm
        // dialog that can either install + restart now or defer until next launch.
        // Routed through IToastNotifier (= WindowsToastNotifier) so the prompt lands in the
        // Windows Notification Center alongside every other app notification (capture, color
        // picked, recording status, etc.) and persists past dismissal — the previous tray
        // balloon disappeared after a few seconds and was easy to miss.
        //
        // "Automatically install updates when available" (default OFF) short-circuits the toast
        // path: when ON and an update lands, we download + apply + restart immediately. The
        // toast still fires for users who haven't opted in.
        var updater = _host.Services.GetRequiredService<AresToys.Updater.UpdaterService>();
        var settingsStoreForUpdate = _host.Services.GetRequiredService<AresToys.Storage.Settings.ISettingsStore>();
        var rawCheckUpdates = await settingsStoreForUpdate.GetAsync("app.updates.check_at_startup", CancellationToken.None).ConfigureAwait(true);
        var checkUpdatesAtStartup = !string.Equals(rawCheckUpdates, "false", StringComparison.OrdinalIgnoreCase);
        var rawAutoInstall = await settingsStoreForUpdate.GetAsync("app.updates.auto_install", CancellationToken.None).ConfigureAwait(true);
        var autoInstall = string.Equals(rawAutoInstall, "true", StringComparison.OrdinalIgnoreCase);
        var updaterLogger = _host.Services.GetService<ILogger<App>>();
        updater.UpdateAvailable += (_, args) =>
        {
            Dispatcher.Invoke(() =>
            {
                if (autoInstall)
                {
                    // User chose unattended updates — download + apply + restart with no prompt.
                    // Wrapped in a Task with try/catch because the previous fire-and-forget
                    // (`_ = updater.DownloadAndRestartAsync(...)`) silently swallowed every
                    // failure: offline, GitHub rate-limit, Velopack staging-folder ACL, all
                    // resulted in "no toast, no install, no log". Now any download/apply
                    // failure falls back to the regular toast so the user can at least retry
                    // manually instead of staring at nothing happening.
                    //
                    // Heads-up toast fires BEFORE the download starts so the user sees a
                    // persistent Action-Center entry explaining what just happened — without
                    // it the app would silently quit + relaunch and a casual user wouldn't
                    // know AresToys hadn't crashed. The toast stays in the notification
                    // history after the auto-update completes, so even if it lands during a
                    // distraction the user can scroll back and see the version transition.
                    notifier.Show(
                        "AresToys updating",
                        $"Auto-updating to version {args.Version}. AresToys will restart in a moment.");
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await updater.DownloadAndRestartAsync(args.Info, CancellationToken.None).ConfigureAwait(false);
                            // ApplyUpdatesAndRestart exits the process before this line — if we
                            // do reach it, something completed without restarting (rare; usually
                            // an internal Velopack early-out).
                        }
                        catch (Exception ex)
                        {
                            updaterLogger?.LogWarning(ex, "Auto-install failed; falling back to toast prompt");
                            Dispatcher.Invoke(() =>
                            {
                                notifier.Show(
                                    "AresToys update available",
                                    $"Version {args.Version} is ready (auto-install failed — click to retry).",
                                    onClick: () => _ = PromptInstallUpdateAsync(updater, args.Info));
                            });
                        }
                    });
                    return;
                }
                notifier.Show(
                    "AresToys update available",
                    $"Version {args.Version} is ready. Click to install.",
                    onClick: () => _ = PromptInstallUpdateAsync(updater, args.Info));
            });
        };
        if (checkUpdatesAtStartup)
        {
            // Fire-and-forget — silent check, errors logged inside the service.
            _ = Task.Run(() => updater.CheckSilentlyAsync(CancellationToken.None));
        }

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
                else if (message.StartsWith(SingleInstanceGuard.CreateWormholePrefix, StringComparison.Ordinal))
                {
                    var path = message[SingleInstanceGuard.CreateWormholePrefix.Length..];
                    HandleCreateWormhole(path);
                }
                else if (string.Equals(message, SingleInstanceGuard.NewWormholeDialogMessage, StringComparison.Ordinal))
                {
                    HandleNewWormholeDialog();
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
        // "Upload with AresToys" and AresToys wasn't running yet. Process the file then sit in the
        // tray; popping the Settings window unsolicited would feel wrong for a one-shot upload.
        if (uploadPath is not null)
        {
            _ = Dispatcher.BeginInvoke(new Action(() => HandleUploadOpen(Path.GetFullPath(uploadPath))),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
        // Cold-start from the folder context-menu — "Create Wormhole" was clicked while AresToys
        // wasn't running. Defer the spawn to Loaded priority so the wormhole manager has
        // finished its own initialization (which itself is queued at Loaded priority above).
        if (createWormholePath is not null)
        {
            _ = Dispatcher.BeginInvoke(new Action(() => HandleCreateWormhole(Path.GetFullPath(createWormholePath))),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
        if (newWormholeDialogRequested)
        {
            _ = Dispatcher.BeginInvoke(new Action(() => HandleNewWormholeDialog()),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
        guard.StartListening();

        // Cold-start from the context menu is silent: tray-only, no Settings popup. Sxcu / sxie
        // still want the main window because their import dialogs need an Owner.
        var startSilent = (uploadPath is not null || createWormholePath is not null || newWormholeDialogRequested)
                          && sxcuPath is null && sxiePath is null;
        // User-controlled "Start minimized" (Settings → Settings tab): same effect as
        // --upload's silent start — tray-only on launch, the user pops the window via the
        // tray icon. Sxcu / sxie cold-starts override because their import dialogs need a
        // visible Owner. Read directly here (not via a VM) since OnStartup runs before the
        // Settings VM is materialised.
        if (!startSilent && sxcuPath is null && sxiePath is null)
        {
            var settingsStoreEarly = _host.Services.GetRequiredService<AresToys.Storage.Settings.ISettingsStore>();
            var startMinRaw = await settingsStoreEarly.GetAsync("app.start_minimized", CancellationToken.None).ConfigureAwait(true);
            if (string.Equals(startMinRaw, "true", StringComparison.OrdinalIgnoreCase)) startSilent = true;
        }
        if (!startSilent) window.Show();
        window.Closing += (sender, args) =>
        {
            args.Cancel = true;
            ((Window)sender!).Hide();
        };

        // Pre-warm the launcher + clipboard singletons so the first hotkey press doesn't pay the
        // construction cost (XAML parse + InitializeComponent + first SQLite read) on the user's
        // critical path. Resolving the service triggers the ctor; PrepareAsync runs in the
        // background to populate the cell grid / row list. By the time the user hits Win+Z or
        // Win+V, Show() paints with content already there — no grey flash.
        //
        // Gated by the per-module flags: if the user has disabled Clipboard or Launcher in
        // Settings → Modules, the pre-warm (and its associated SQLite reads + icon cache spin-up
        // via SHGetFileInfo) is skipped entirely. The DI registration stays in place so any
        // direct .GetRequiredService<…>() call still resolves a window — graceful degradation
        // for opening the disabled module through an unexpected code path.
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, async () =>
        {
            var services = _host!.Services;
            if (modules.LauncherEnabled)
            {
                try
                {
                    var launcher = services.GetRequiredService<AresToys.App.Views.LauncherWindow>();
                    await launcher.PrepareAsync();
                }
                catch (Exception ex)
                {
                    services.GetService<ILogger<App>>()?.LogDebug(ex, "Launcher pre-warm failed; first open will load lazily");
                }
            }
            if (modules.ClipboardEnabled)
            {
                try
                {
                    var clipboard = services.GetRequiredService<AresToys.App.Views.ClipboardWindow>();
                    await clipboard.PrepareAsync();
                }
                catch (Exception ex)
                {
                    services.GetService<ILogger<App>>()?.LogDebug(ex, "Clipboard pre-warm failed; first open will load lazily");
                }
            }
        });

        var helper = new WindowInteropHelper(window);
        helper.EnsureHandle();
        var source = HwndSource.FromHwnd(helper.Handle)!;
        source.AddHook(WndProc);

        var hotkeyConfig = _host.Services.GetRequiredService<AresToys.App.Services.Hotkeys.HotkeyConfigService>();
        var hotkeyLogger = _host.Services.GetRequiredService<ILogger<App>>();

        // Single low-level keyboard hook for ALL global hotkeys (popup, region capture, color picker,
        // recording, ...). Hook-based registration wins against any foreground app's keyboard
        // shortcut and even against OS-reserved combos like Win+Shift+S — same approach PowerToys
        // KeyboardManager uses. In-app shortcuts (popup Ctrl+P / Enter / Ctrl+1-9, editor bindings)
        // stay focus-local via the WPF Window's KeyDown handlers — those don't pass through here.
        _keyboardHook = _host.Services.GetRequiredService<KeyboardHook>();

        // Every hotkey now invokes a workflow (pipeline profile) by id. The 1:1 mapping between
        // hotkey id and workflow id is intentional: HotkeyConfigService's Catalog is built from
        // profiles, so the ids are workflow ids. WorkflowRunner loads the profile and runs its steps.
        var workflowRunner = _host.Services.GetRequiredService<WorkflowRunner>();
        Action MakeCallback(string workflowId)
            => () => Dispatcher.InvokeAsync(() => _ = workflowRunner.RunAsync(workflowId, CancellationToken.None));

        async Task RegisterCatalogAsync(string id)
        {
            // Skip hotkey registration for hotkeys that belong to a disabled module — turning
            // the module off means the global keyboard hook never even sees the binding, so
            // the user can reclaim the shortcut for another app or workflow without conflict.
            if (IsHotkeyForDisabledModule(id, modules))
            {
                hotkeyLogger.LogInformation("Hotkey {Id} skipped — module disabled", id);
                return;
            }
            var def = await hotkeyConfig.GetEffectiveAsync(id, CancellationToken.None);
            if (def.VirtualKey == 0)
            {
                hotkeyLogger.LogInformation("Hotkey {Id} unbound — skipping registration", id);
                return;
            }
            _keyboardHook.Register(id, def.Modifiers, def.VirtualKey, MakeCallback(id), suppress: true);
            hotkeyLogger.LogInformation("Hotkey {Id} bound to {Combo}",
                id, AresToys.App.Services.Hotkeys.HotkeyDisplay.Format(def.Modifiers, def.VirtualKey));
        }

        var catalog = await hotkeyConfig.GetCatalogAsync(CancellationToken.None);
        foreach (var entry in catalog)
            await RegisterCatalogAsync(entry.Id);

        // Settings UI raises Changed when the user rebinds, clears, or resets → keep the hook in
        // sync. VK 0 means the user cleared the binding, so unregister and don't re-register.
        // Hotkeys for disabled modules stay unregistered even on rebind — the module flag is the
        // hard gate; the binding edit persists but doesn't take effect until the module is
        // re-enabled + the app restarted.
        hotkeyConfig.Changed += (_, def) =>
        {
            _keyboardHook.Unregister(def.Id);
            if (def.VirtualKey == 0)
            {
                hotkeyLogger.LogInformation("Hotkey {Id} cleared", def.Id);
                return;
            }
            if (IsHotkeyForDisabledModule(def.Id, modules))
            {
                hotkeyLogger.LogInformation("Hotkey {Id} rebind ignored — module disabled", def.Id);
                return;
            }
            _keyboardHook.Register(def.Id, def.Modifiers, def.VirtualKey, MakeCallback(def.Id), suppress: true);
            // Special keys whose KEYDOWN is consumed by Windows before reaching the LL hook —
            // we use their KEYUP as trigger (see KeyboardHook HookProc). When the rebind itself
            // came from the user pressing this key inside HotkeyCaptureWindow, its KEYUP races
            // straight into the freshly-registered binding and fires the workflow once. Swallow
            // that single residual keyup so the trigger only counts the user's NEXT press.
            const uint VK_SNAPSHOT = 0x2C;
            const uint VK_PAUSE    = 0x13;
            if (def.VirtualKey is VK_SNAPSHOT or VK_PAUSE)
                _keyboardHook.SuppressNextKeyUp(def.VirtualKey);
            hotkeyLogger.LogInformation("Hotkey {Id} re-bound to {Combo}",
                def.Id, AresToys.App.Services.Hotkeys.HotkeyDisplay.Format(def.Modifiers, def.VirtualKey));
        };

        // Bootstrap KeySequences module before the hook is installed — the tracker registers a
        // stream listener which the hook will then start invoking. Loading the runtime settings,
        // hydrating the workflow-triggers provider, and building the initial matcher index are
        // all idempotent / cheap when the module is off (the listener simply isn't registered).
        if (modules.KeySequencesEnabled)
        {
            try
            {
                var ksSettings = _host.Services.GetRequiredService<AresToys.App.Services.KeySequences.KeySequenceModuleSettings>();
                await ksSettings.LoadAsync(CancellationToken.None);
                var workflowProvider = _host.Services.GetRequiredService<AresToys.App.Services.KeySequences.WorkflowSequenceProvider>();
                await workflowProvider.LoadAsync(CancellationToken.None);
                var bindingStore = _host.Services.GetRequiredService<AresToys.App.Services.KeySequences.SequenceBindingStore>();
                bindingStore.Rebuild();
                var tracker = _host.Services.GetRequiredService<AresToys.App.Services.KeySequences.KeySequenceTracker>();
                tracker.ApplyEnabledState();
                hotkeyLogger.LogInformation("KeySequences module bootstrapped — tracker listening on stream events.");
            }
            catch (Exception ex)
            {
                hotkeyLogger.LogError(ex, "KeySequences module bootstrap failed — module disabled for this session.");
            }
        }

        try { _keyboardHook.Install(); hotkeyLogger.LogInformation("Low-level keyboard hook installed for global hotkeys."); }
        catch (Exception ex) { hotkeyLogger.LogWarning(ex, "Failed to install keyboard hook"); }

        // The clipboard ingestion service hooks the Win32 clipboard chain and watches for every
        // copy event in the system — this is the single biggest "always-on" cost of the
        // clipboard module. Gated on the module flag: turning Clipboard off in Settings means
        // zero clipboard interception, zero SQLite writes on copy, zero "on-clipboard" workflow
        // executions. The hotkey for Win+V is also skipped further up, so the user truly stops
        // paying for the module.
        if (modules.ClipboardEnabled)
        {
            var ingestion = _host.Services.GetRequiredService<ClipboardIngestionService>();
            ingestion.Start(helper.Handle);
        }
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
                MessageBox.Show(owner, $"File not found:\n{path}", "AresToys",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var json = File.ReadAllText(path);
            var config = AresToys.CustomUploaders.CustomUploaderConfigLoader.Parse(json);
            if (config is null || !AresToys.CustomUploaders.CustomUploaderConfigLoader.IsValid(config))
            {
                MessageBox.Show(owner,
                    $"This file isn't a valid .sxcu — it's missing the required Name or RequestURL fields.\n\n{path}",
                    "AresToys", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var dlg = new AresToys.App.Views.SxcuImportDialog(path, config) { Owner = owner };
            if (dlg.ShowDialog() == true && dlg.InstalledPath is not null)
            {
                MessageBox.Show(owner,
                    $"Installed to:\n{dlg.InstalledPath}\n\nRestart AresToys to load the new uploader.",
                    "AresToys", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"Couldn't open .sxcu file:\n{ex.Message}",
                "AresToys", MessageBoxButton.OK, MessageBoxImage.Error);
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
                MessageBox.Show(owner, $"File not found:\n{path}", "AresToys",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Reuse an open editor if there is one — Application.Windows includes any
            // ImageEffectsWindow opened from Settings or from a previous .sxie association.
            var existing = Application.Current.Windows
                .OfType<AresToys.App.Views.ImageEffectsWindow>()
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
            var presetStore = _host!.Services.GetRequiredService<AresToys.Storage.ImageEffects.IImageEffectPresetStore>();
            var settingsStore = _host.Services.GetRequiredService<AresToys.Storage.Settings.ISettingsStore>();
            var vm = new AresToys.App.ViewModels.ImageEffects.ImageEffectsViewModel(
                AresToys.ImageEffects.ImageEffectRegistry.Default, presetStore);
            var window = new AresToys.App.Views.ImageEffectsWindow(vm, settingsStore) { Owner = owner };
            window.Show();
            await vm.ImportSxieFileAsync(path).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"Couldn't open .sxie file:\n{ex.Message}",
                "AresToys", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Route a file path from the Explorer "Upload with AresToys" verb through the
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
                MessageBox.Show($"File not found:\n{path}", "AresToys",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var manual = _host?.Services.GetService(typeof(ManualUploadService)) as ManualUploadService;
            if (manual is null)
            {
                MessageBox.Show("AresToys isn't fully initialised yet — try again in a moment.",
                    "AresToys", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var settings = _host?.Services.GetService(typeof(AresToys.Storage.Settings.ISettingsStore)) as AresToys.Storage.Settings.ISettingsStore;
            _ = Task.Run(async () =>
            {
                // Resolve the profile id off the UI thread (settings reads hit SQLite). Empty / null
                // → default profile, same fallback ManualUploadService applies for unknown ids.
                var profileId = settings is null
                    ? AresToys.Pipeline.Profiles.DefaultPipelineProfiles.ManualUploadId
                    : (await settings.GetAsync(ExplorerContextMenuWorkflowKey, CancellationToken.None).ConfigureAwait(false))
                        is { Length: > 0 } stored
                            ? stored
                            : AresToys.Pipeline.Profiles.DefaultPipelineProfiles.ManualUploadId;
                await manual.UploadFileToProfileAsync(path, profileId, CancellationToken.None).ConfigureAwait(false);
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't start upload:\n{ex.Message}",
                "AresToys", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Spawn a wormhole pointing at <paramref name="folderPath"/>. Called from the
    /// Explorer "Create Wormhole" folder right-click verb (cold-start via <c>--create-wormhole</c>
    /// CLI or warm-forward via <see cref="SingleInstanceGuard.CreateWormholePrefix"/> on the
    /// IPC pipe). Title defaults to the folder name; no dialog — the user already expressed
    /// intent by clicking the menu entry. Wormholes module is auto-enabled if it was off.</summary>
    private void HandleCreateWormhole(string folderPath)
    {
        try
        {
            if (!Directory.Exists(folderPath))
            {
                MessageBox.Show($"Folder not found:\n{folderPath}", "AresToys",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var manager = _host?.Services.GetService(typeof(AresToys.App.Services.Wormholes.IWormholeWindowManager))
                as AresToys.App.Services.Wormholes.IWormholeWindowManager;
            if (manager is null)
            {
                MessageBox.Show("AresToys isn't fully initialised yet — try again in a moment.",
                    "AresToys", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // If the module is currently off, flip it on for this process so the manager will
            // actually spawn a window. Persisting the flag here means a future cold start picks
            // it up automatically — the user clicked "Create Wormhole" so they want it enabled.
            var modules = _host?.Services.GetService(typeof(ModuleSettings)) as ModuleSettings;
            if (modules is not null && !modules.WormholesEnabled)
            {
                modules.WormholesEnabled = true;
                var settingsStore = _host?.Services.GetService(typeof(AresToys.Storage.Settings.ISettingsStore))
                    as AresToys.Storage.Settings.ISettingsStore;
                _ = settingsStore?.SetAsync(ModuleSettings.WormholesKey, "true", sensitive: false, CancellationToken.None);
                // The manager's InitializeAsync only runs once at startup; if Wormholes was off
                // at boot we never spawned its DI graph. Trigger init now so the new wormhole
                // joins a live manager.
                _ = manager.InitializeAsync(CancellationToken.None);
            }
            var title = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(title)) title = "Wormhole";
            _ = manager.CreateAsync(title, folderPath, CancellationToken.None);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't create wormhole:\n{ex.Message}",
                "AresToys", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Open <see cref="AresToys.App.Views.NewWormholeDialog"/> so the user picks a
    /// source folder, then spawn the wormhole. Triggered by the Background variant of the
    /// shell verb — right-click on empty area, where the current folder is rarely what the
    /// user wants to mirror. Pairs with <see cref="HandleCreateWormhole"/> for the auto-create
    /// case (Item variant — right-click on a folder icon).</summary>
    private void HandleNewWormholeDialog()
    {
        try
        {
            var manager = _host?.Services.GetService(typeof(AresToys.App.Services.Wormholes.IWormholeWindowManager))
                as AresToys.App.Services.Wormholes.IWormholeWindowManager;
            if (manager is null)
            {
                MessageBox.Show("AresToys isn't fully initialised yet — try again in a moment.",
                    "AresToys", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // Auto-enable Wormholes module if off — the user clicked the menu so they want it on.
            var modules = _host?.Services.GetService(typeof(ModuleSettings)) as ModuleSettings;
            if (modules is not null && !modules.WormholesEnabled)
            {
                modules.WormholesEnabled = true;
                var settingsStore = _host?.Services.GetService(typeof(AresToys.Storage.Settings.ISettingsStore))
                    as AresToys.Storage.Settings.ISettingsStore;
                _ = settingsStore?.SetAsync(ModuleSettings.WormholesKey, "true", sensitive: false, CancellationToken.None);
                _ = manager.InitializeAsync(CancellationToken.None);
            }

            var dlg = new AresToys.App.Views.NewWormholeDialog();
            if (dlg.ShowDialog() != true || dlg.Result is null) return;
            var choice = dlg.Result;
            _ = manager.CreateAsync(choice.Title, choice.SourceFolder, CancellationToken.None);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't open the New Wormhole dialog:\n{ex.Message}",
                "AresToys", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Settings key carrying the pipeline profile id triggered by the Explorer
    /// "Upload with AresToys" verb. Empty / unset → <c>manual-upload</c>. Kept here so the App
    /// handler and the Settings UI stay in sync.</summary>
    public const string ExplorerContextMenuWorkflowKey = "explorer.context_menu.workflow";

    /// <summary>True when the hotkey id belongs to a feature module currently disabled in
    /// <see cref="ModuleSettings"/>. Used by the hotkey registration loop to skip a binding
    /// outright (the global keyboard hook never sees the combo, leaving the shortcut free for
    /// the user's other apps). Mapping is intentionally explicit + small — every module owns at
    /// most a couple of workflow ids and a static table keeps the gating predictable.</summary>
    private static bool IsHotkeyForDisabledModule(string hotkeyId, ModuleSettings modules)
    {
        // Clipboard module owns the Win+V popup. The "on-clipboard" automatic workflow is not a
        // hotkey (fired by ClipboardIngestionService events), so gating Ingestion above covers it.
        // Incognito is a clipboard-capture suppression toggle — also pointless without ingestion.
        if (!modules.ClipboardEnabled &&
            (hotkeyId == AresToys.Pipeline.Profiles.DefaultPipelineProfiles.ShowPopupId
             || hotkeyId == AresToys.Pipeline.Profiles.DefaultPipelineProfiles.ToggleIncognitoId))
            return true;
        // Launcher module owns the open-launcher workflow.
        if (!modules.LauncherEnabled && hotkeyId == AresToys.Pipeline.Profiles.DefaultPipelineProfiles.OpenLauncherId)
            return true;
        // Wormholes hotkeys (new-wormhole, hide-all) will join this table when the feature
        // ships; until then there's nothing for them to gate.
        return false;
    }

    /// <summary>Show a confirm dialog for an available update. OK → download + apply + restart.
    /// Cancel → leave it for the next launch (Velopack will offer it again on the next silent
    /// check). Used by both the toast click and the Settings → "Check for updates" button.</summary>
    internal static async Task PromptInstallUpdateAsync(AresToys.Updater.UpdaterService updater, Velopack.UpdateInfo info)
    {
        var version = info.TargetFullRelease.Version.ToString();
        var choice = MessageBox.Show(
            $"AresToys {version} is available.\n\nDownload and restart now?",
            "Update AresToys",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information,
            MessageBoxResult.OK);
        if (choice != MessageBoxResult.OK) return;
        try { await updater.DownloadAndRestartAsync(info, CancellationToken.None).ConfigureAwait(true); }
        catch (Exception ex)
        {
            MessageBox.Show($"Update failed:\n{ex.Message}", "AresToys",
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
            // Close any live wormhole windows so their Closing handler doesn't try to persist
            // mid-shutdown. The records are already in sync (every geometry change flushes); a
            // no-op CloseAll on a module that was never initialised is intentional.
            _host.Services.GetService<AresToys.App.Services.Wormholes.IWormholeWindowManager>()?.CloseAll();
            _host.Services.GetService<SingleInstanceGuard>()?.Dispose();
            _host.Services.GetService<ExternalTextEditorService>()?.Dispose();
            await _host.StopAsync(TimeSpan.FromSeconds(2));
            _host.Dispose();
        }
        base.OnExit(e);
    }

}
