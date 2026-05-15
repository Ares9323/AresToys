using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AresToys.App.Services;
using AresToys.App.Services.Plugins;
using AresToys.Storage.Settings;

namespace AresToys.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private const string StartMinimizedKey = "app.start_minimized";
    private const string CheckUpdatesAtStartupKey = "app.updates.check_at_startup";
    private const string AutoInstallUpdatesKey = "app.updates.auto_install";
    private const string EditorStartMaximizedKey = "app.editor_start_maximized";
    private const string EditorAltClickNoMatchKey = "editor.alt_click_no_match";
    private const string ClipboardShowSnippetWithLabelKey = "clipboard.show_snippet_with_label";
    // Module-toggle keys shared with ModuleSettings (kept in lockstep — App.OnStartup reads the
    // same constants on launch to decide whether to spin each module up).
    private const string ClipboardModuleKey  = ModuleSettings.ClipboardKey;
    private const string LauncherModuleKey   = ModuleSettings.LauncherKey;
    private const string WormholesModuleKey  = ModuleSettings.WormholesKey;
    private readonly AutostartService _autostart;
    private readonly ISettingsStore _settingsStore;
    private readonly PopupWindowViewModel _clipboardVm;

    public SettingsViewModel(
        PluginRegistry registry,
        AutostartService autostart,
        ISettingsStore settingsStore,
        UploadersViewModel uploaders,
        HotkeysViewModel hotkeys,
        CaptureDefaultsViewModel capture,
        WorkflowsViewModel workflows,
        ThemeViewModel theme,
        CategoriesViewModel categories,
        DebugViewModel debug,
        WormholesViewModel wormholes,
        PopupWindowViewModel clipboardVm)
    {
        _autostart = autostart;
        _settingsStore = settingsStore;
        _clipboardVm = clipboardVm;
        Theme = theme;
        Categories = categories;
        Debug = debug;
        Wormholes = wormholes;
        // Initial state mirrors the registry. Future toggles persist via OnStartWithWindowsChanged.
        _suppressAutostartPersist = true;
        StartWithWindows = autostart.IsEnabled;
        _suppressAutostartPersist = false;

        // StartMinimized + EditorStartMaximized hydrate async from the SQLite settings store.
        // The fire-and-forget is intentional: SettingsViewModel is constructed eagerly during
        // DI, before the user has navigated to the Settings tab, so we don't block startup
        // waiting for values most users will never look at on this launch. Suppress flags
        // prevent the initial assignment from round-tripping back to disk with the same value.
        _ = LoadPersistedSettingsAsync();
        Uploaders = uploaders;
        // Hotkeys "Add custom workflow" button → run the Add flow (no modal — auto-default name)
        // then drop straight into the edit view with the inline name field focused + selected
        // so the user can type the real name immediately.
        hotkeys.AddCustomWorkflowRequested += async (_, _) =>
        {
            await workflows.ReloadWorkflowsAsync().ConfigureAwait(true);
            await workflows.AddWorkflowCommand.ExecuteAsync(null).ConfigureAwait(true);
            if (workflows.SelectedWorkflow is { } added)
                await hotkeys.BeginEditAsync(added.Id, focusName: true).ConfigureAwait(true);
        };
        Hotkeys = hotkeys;
        Capture = capture;
        Workflows = workflows;
        // Reads the same string that MSBuild's -p:Version was given (suffix included). Falls
        // back to the 4-part numeric version when the InformationalVersion attribute is
        // missing — defensive only, the SDK always emits one.
        var asm = typeof(SettingsViewModel).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        // SourceLink appends "+<git-sha>" to InformationalVersion in modern .NET SDKs; keep
        // just the leading semver part so About reads "0.1.0-dev" not "0.1.0-dev+abc1234".
        var plus = info?.IndexOf('+') ?? -1;
        AppVersion = plus >= 0 ? info![..plus] : (info ?? asm.GetName().Version?.ToString() ?? "dev");
        // Hold a reference so DI doesn't garbage-collect the registry singleton through this VM.
        _ = registry;
    }

    public UploadersViewModel Uploaders { get; }
    public HotkeysViewModel Hotkeys { get; }
    public CaptureDefaultsViewModel Capture { get; }
    public WorkflowsViewModel Workflows { get; }
    public ThemeViewModel Theme { get; }
    public CategoriesViewModel Categories { get; }
    public DebugViewModel Debug { get; }
    public WormholesViewModel Wormholes { get; }

    [ObservableProperty]
    private SettingsTab _selectedTab = SettingsTab.Hotkeys;

    /// <summary>Bound to the Settings-tab "Run AresToys when Windows starts" checkbox. Reads /
    /// writes HKCU Run via <see cref="AutostartService"/>. Constructor seeds it from the current
    /// registry value (no flicker), then user toggles propagate immediately.</summary>
    [ObservableProperty]
    private bool _startWithWindows;

    private bool _suppressAutostartPersist;

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_suppressAutostartPersist) return;
        _autostart.SetEnabled(value);
    }

    /// <summary>Bound to the Settings-tab "Start minimized" checkbox. Persisted in the SQLite
    /// settings store under <see cref="StartMinimizedKey"/>; <see cref="App.OnStartup"/> reads
    /// it before deciding whether to call <c>window.Show()</c> on launch.</summary>
    [ObservableProperty]
    private bool _startMinimized;

    /// <summary>Bound to "Look for updates at startup" (default ON). Persisted under
    /// <see cref="CheckUpdatesAtStartupKey"/>; <see cref="App.OnStartup"/> reads it before
    /// firing the silent <c>UpdaterService.CheckSilentlyAsync</c> call. Turning it off skips
    /// the GitHub API roundtrip entirely — manual checks from the About tab keep working.</summary>
    [ObservableProperty]
    private bool _checkUpdatesAtStartup = true;

    /// <summary>Bound to "Automatically install updates when available" (default OFF, safe).
    /// Persisted under <see cref="AutoInstallUpdatesKey"/>. When ON, an available update from
    /// the silent startup check downloads + applies + restarts without the toast prompt;
    /// when OFF the existing toast appears so the user picks the timing. Gated behind the
    /// startup check — if that's off, this flag has no effect.</summary>
    [ObservableProperty]
    private bool _autoInstallUpdates;

    /// <summary>Bound to the Settings-tab "Start editor maximized" checkbox. Persisted under
    /// <see cref="EditorStartMaximizedKey"/>. Honoured by <c>EditorLauncher.OpenAsync</c> (the
    /// non-pipeline open path: toast click, history → edit). Workflow tasks have their own
    /// <c>fullscreen</c> JSON config which is read inside <c>EditAsync</c> and overrides this
    /// — the user's per-task choice always wins over the global default.</summary>
    [ObservableProperty]
    private bool _editorStartMaximized;

    /// <summary>Bound to the Editor-section "Alt+click selects any shape" toggle. Persisted
    /// under <see cref="EditorAltClickNoMatchKey"/> as <c>"select_any"</c> (true) or
    /// <c>"place"</c> (false / default). Read by <see cref="Services.EditorLauncher"/> on each
    /// editor open and pushed into the editor view-model's <c>AltClickFallback</c> property.
    /// Governs only the no-match fallback: alt+click on a same-type hit always selects.
    /// (Renamed from EditorShiftClickSelectAny in 0.1.6 — Shift was reassigned to multi-select-
    /// toggle so it stacks with Alt: Alt+Shift = select-and-add-to-set.)</summary>
    [ObservableProperty]
    private bool _editorAltClickSelectAny;

    /// <summary>Bound to the Settings-tab "Show content snippet under label" checkbox.
    /// Persisted under <see cref="ClipboardShowSnippetWithLabelKey"/>. On change the new
    /// value is pushed directly into <see cref="PopupWindowViewModel.ShowSnippetWithLabel"/>
    /// so an already-open clipboard window updates its rows immediately — the popup VM is a
    /// singleton and its own setter re-persists harmlessly to the same key.</summary>
    [ObservableProperty]
    private bool _clipboardShowSnippetWithLabel;

    /// <summary>Bound to the Settings → Modules "Clipboard" toggle. Persisted under
    /// <see cref="ClipboardModuleKey"/> — read by <see cref="App.OnStartup"/> to decide whether
    /// to start <see cref="Services.ClipboardIngestionService"/>, pre-warm the popup window, and
    /// register the Win+V hotkey. Toggling at runtime persists immediately but the resulting
    /// behaviour change only lands on the next launch, signalled to the user via the
    /// <see cref="ModulesRestartRequired"/> hint.</summary>
    [ObservableProperty]
    private bool _clipboardModuleEnabled = true;

    /// <summary>Bound to the Settings → Modules "Launcher" toggle. Same lifecycle as
    /// <see cref="ClipboardModuleEnabled"/> — gates pre-warm of <see cref="Views.LauncherWindow"/>
    /// + the launcher hotkey + the tray menu entry.</summary>
    [ObservableProperty]
    private bool _launcherModuleEnabled = true;

    /// <summary>Bound to the Settings → Modules "Wormholes" toggle. Wormholes is the desktop-
    /// fence subsystem currently in development (see docs/WormholesSpec.md). The toggle persists
    /// here so existing installs can opt in ahead of the feature shipping; the gates that read
    /// this flag in <see cref="App.OnStartup"/> are currently no-ops.</summary>
    [ObservableProperty]
    private bool _wormholesModuleEnabled;

    /// <summary>True while at least one module toggle has been changed in the current session.
    /// Bound to a hint label below the toggle group that prompts the user to restart for the
    /// changes to take effect — the gates fire once on startup, so live tear-down isn't
    /// supported and the hint is the honest answer.</summary>
    [ObservableProperty]
    private bool _modulesRestartRequired;

    private bool _suppressStartMinimizedPersist;
    private bool _suppressCheckUpdatesAtStartupPersist;
    private bool _suppressAutoInstallUpdatesPersist;
    private bool _suppressEditorStartMaximizedPersist;
    private bool _suppressEditorAltClickSelectAnyPersist;
    private bool _suppressClipboardShowSnippetWithLabelPersist;
    private bool _suppressClipboardModulePersist;
    private bool _suppressLauncherModulePersist;
    private bool _suppressWormholesModulePersist;

    private async Task LoadPersistedSettingsAsync()
    {
        var rawMin = await _settingsStore.GetAsync(StartMinimizedKey, CancellationToken.None).ConfigureAwait(true);
        _suppressStartMinimizedPersist = true;
        try { StartMinimized = string.Equals(rawMin, "true", StringComparison.OrdinalIgnoreCase); }
        finally { _suppressStartMinimizedPersist = false; }

        // "Look for updates at startup" defaults ON when the key is missing (fresh install,
        // pre-0.1.11 upgrade) — matches the prior behaviour where the silent check always ran.
        var rawCheckUpdates = await _settingsStore.GetAsync(CheckUpdatesAtStartupKey, CancellationToken.None).ConfigureAwait(true);
        _suppressCheckUpdatesAtStartupPersist = true;
        try { CheckUpdatesAtStartup = !string.Equals(rawCheckUpdates, "false", StringComparison.OrdinalIgnoreCase); }
        finally { _suppressCheckUpdatesAtStartupPersist = false; }

        // "Auto-install updates" defaults OFF — opt-in only, since auto-install + restart is a
        // surprise the user shouldn't get unless they explicitly asked for it.
        var rawAutoInstall = await _settingsStore.GetAsync(AutoInstallUpdatesKey, CancellationToken.None).ConfigureAwait(true);
        _suppressAutoInstallUpdatesPersist = true;
        try { AutoInstallUpdates = string.Equals(rawAutoInstall, "true", StringComparison.OrdinalIgnoreCase); }
        finally { _suppressAutoInstallUpdatesPersist = false; }

        var rawMax = await _settingsStore.GetAsync(EditorStartMaximizedKey, CancellationToken.None).ConfigureAwait(true);
        _suppressEditorStartMaximizedPersist = true;
        try { EditorStartMaximized = string.Equals(rawMax, "true", StringComparison.OrdinalIgnoreCase); }
        finally { _suppressEditorStartMaximizedPersist = false; }

        var rawAlt = await _settingsStore.GetAsync(EditorAltClickNoMatchKey, CancellationToken.None).ConfigureAwait(true);
        _suppressEditorAltClickSelectAnyPersist = true;
        try { EditorAltClickSelectAny = string.Equals(rawAlt, "select_any", StringComparison.OrdinalIgnoreCase); }
        finally { _suppressEditorAltClickSelectAnyPersist = false; }

        var rawSnippet = await _settingsStore.GetAsync(ClipboardShowSnippetWithLabelKey, CancellationToken.None).ConfigureAwait(true);
        _suppressClipboardShowSnippetWithLabelPersist = true;
        try { ClipboardShowSnippetWithLabel = rawSnippet == "1"; }
        finally { _suppressClipboardShowSnippetWithLabelPersist = false; }

        // Module flags share the "unset → default" semantics with App.OnStartup: Clipboard +
        // Launcher default ON when the key is missing, Wormholes defaults OFF (opt-in).
        var rawClipboardModule = await _settingsStore.GetAsync(ClipboardModuleKey, CancellationToken.None).ConfigureAwait(true);
        _suppressClipboardModulePersist = true;
        try { ClipboardModuleEnabled = !string.Equals(rawClipboardModule, "false", StringComparison.OrdinalIgnoreCase); }
        finally { _suppressClipboardModulePersist = false; }

        var rawLauncherModule = await _settingsStore.GetAsync(LauncherModuleKey, CancellationToken.None).ConfigureAwait(true);
        _suppressLauncherModulePersist = true;
        try { LauncherModuleEnabled = !string.Equals(rawLauncherModule, "false", StringComparison.OrdinalIgnoreCase); }
        finally { _suppressLauncherModulePersist = false; }

        var rawWormholesModule = await _settingsStore.GetAsync(WormholesModuleKey, CancellationToken.None).ConfigureAwait(true);
        _suppressWormholesModulePersist = true;
        try { WormholesModuleEnabled = string.Equals(rawWormholesModule, "true", StringComparison.OrdinalIgnoreCase); }
        finally { _suppressWormholesModulePersist = false; }
    }

    partial void OnStartMinimizedChanged(bool value)
    {
        if (_suppressStartMinimizedPersist) return;
        _ = _settingsStore.SetAsync(StartMinimizedKey,
            value ? "true" : "false",
            sensitive: false,
            CancellationToken.None);
    }

    partial void OnCheckUpdatesAtStartupChanged(bool value)
    {
        if (_suppressCheckUpdatesAtStartupPersist) return;
        _ = _settingsStore.SetAsync(CheckUpdatesAtStartupKey,
            value ? "true" : "false",
            sensitive: false,
            CancellationToken.None);
    }

    partial void OnAutoInstallUpdatesChanged(bool value)
    {
        if (_suppressAutoInstallUpdatesPersist) return;
        _ = _settingsStore.SetAsync(AutoInstallUpdatesKey,
            value ? "true" : "false",
            sensitive: false,
            CancellationToken.None);
    }

    partial void OnEditorStartMaximizedChanged(bool value)
    {
        if (_suppressEditorStartMaximizedPersist) return;
        _ = _settingsStore.SetAsync(EditorStartMaximizedKey,
            value ? "true" : "false",
            sensitive: false,
            CancellationToken.None);
    }

    partial void OnEditorAltClickSelectAnyChanged(bool value)
    {
        if (_suppressEditorAltClickSelectAnyPersist) return;
        _ = _settingsStore.SetAsync(EditorAltClickNoMatchKey,
            value ? "select_any" : "place",
            sensitive: false,
            CancellationToken.None);
    }

    partial void OnClipboardShowSnippetWithLabelChanged(bool value)
    {
        if (_suppressClipboardShowSnippetWithLabelPersist) return;
        _ = _settingsStore.SetAsync(ClipboardShowSnippetWithLabelKey,
            value ? "1" : "0",
            sensitive: false,
            CancellationToken.None);
        // Push into the clipboard VM so an already-visible window refreshes its rows now,
        // without waiting for the next Hide→Show cycle to re-read from SQLite.
        _clipboardVm.ShowSnippetWithLabel = value;
    }

    partial void OnClipboardModuleEnabledChanged(bool value)
    {
        if (_suppressClipboardModulePersist) return;
        _ = _settingsStore.SetAsync(ClipboardModuleKey,
            value ? "true" : "false",
            sensitive: false,
            CancellationToken.None);
        ModulesRestartRequired = true;
    }

    partial void OnLauncherModuleEnabledChanged(bool value)
    {
        if (_suppressLauncherModulePersist) return;
        _ = _settingsStore.SetAsync(LauncherModuleKey,
            value ? "true" : "false",
            sensitive: false,
            CancellationToken.None);
        ModulesRestartRequired = true;
    }

    partial void OnWormholesModuleEnabledChanged(bool value)
    {
        if (_suppressWormholesModulePersist) return;
        _ = _settingsStore.SetAsync(WormholesModuleKey,
            value ? "true" : "false",
            sensitive: false,
            CancellationToken.None);
        ModulesRestartRequired = true;
    }

    public bool IsUploadersSelected => SelectedTab == SettingsTab.Uploaders;
    public bool IsHotkeysSelected   => SelectedTab == SettingsTab.Hotkeys;
    public bool IsCaptureSelected   => SelectedTab == SettingsTab.Capture;
    public bool IsThemeSelected      => SelectedTab == SettingsTab.Theme;
    public bool IsCategoriesSelected => SelectedTab == SettingsTab.Categories;
    public bool IsWormholesSelected  => SelectedTab == SettingsTab.Wormholes;
    public bool IsSettingsSelected   => SelectedTab == SettingsTab.Settings;
    public bool IsDebugSelected     => SelectedTab == SettingsTab.Debug;
    public bool IsAboutSelected     => SelectedTab == SettingsTab.About;

    public string AppVersion { get; }

    partial void OnSelectedTabChanged(SettingsTab value)
    {
        OnPropertyChanged(nameof(IsUploadersSelected));
        OnPropertyChanged(nameof(IsHotkeysSelected));
        OnPropertyChanged(nameof(IsCaptureSelected));
        OnPropertyChanged(nameof(IsThemeSelected));
        OnPropertyChanged(nameof(IsCategoriesSelected));
        OnPropertyChanged(nameof(IsWormholesSelected));
        OnPropertyChanged(nameof(IsSettingsSelected));
        OnPropertyChanged(nameof(IsDebugSelected));
        OnPropertyChanged(nameof(IsAboutSelected));
        if (value == SettingsTab.Uploaders) _ = Uploaders.ReloadAsync();
        if (value == SettingsTab.Hotkeys) _ = Hotkeys.ReloadAsync();
        if (value == SettingsTab.Wormholes) _ = Wormholes.ReloadAsync();
    }

    [RelayCommand] private void ShowUploaders() => SelectedTab = SettingsTab.Uploaders;
    [RelayCommand] private void ShowHotkeys()   => SelectedTab = SettingsTab.Hotkeys;
    [RelayCommand] private void ShowCapture()   => SelectedTab = SettingsTab.Capture;
    [RelayCommand] private void ShowTheme()      => SelectedTab = SettingsTab.Theme;
    [RelayCommand] private void ShowCategories() => SelectedTab = SettingsTab.Categories;
    [RelayCommand] private void ShowWormholes()  => SelectedTab = SettingsTab.Wormholes;
    [RelayCommand] private void ShowSettings()   => SelectedTab = SettingsTab.Settings;
    [RelayCommand] private void ShowDebug()     => SelectedTab = SettingsTab.Debug;
    [RelayCommand] private void ShowAbout()     => SelectedTab = SettingsTab.About;
}

public enum SettingsTab { Uploaders, Hotkeys, Capture, Theme, Categories, Wormholes, Settings, Debug, About }
