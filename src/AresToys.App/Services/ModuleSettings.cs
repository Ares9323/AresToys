namespace AresToys.App.Services;

/// <summary>
/// Runtime-resolved enabled state for the optional feature modules (Clipboard, Launcher,
/// Wormholes). Populated once during <c>App.OnStartup</c> from the persisted settings keys
/// below, then read by every gate point (eager pre-warm, ingestion service start, hotkey
/// registration loop, tray menu builder) to decide whether to spin a module up.
///
/// Toggles from the Settings UI persist to <see cref="AresToys.Storage.Settings.ISettingsStore"/>
/// and take effect at the next launch — the gates only run once at startup, so changing a flag
/// mid-process can't tear down a running ingestion thread or unhook a hotkey without re-entering
/// the startup path. The Settings UI surfaces a "restart required" hint on toggle.
///
/// Defaults: Clipboard ON, Launcher ON (preserve historical behaviour for existing installs),
/// Wormholes OFF (feature still in development; users who don't want desktop fences pay zero).
/// </summary>
public sealed class ModuleSettings
{
    public bool ClipboardEnabled { get; set; } = true;
    public bool LauncherEnabled { get; set; } = true;
    public bool WormholesEnabled { get; set; }
    public bool KeySequencesEnabled { get; set; }

    public const string ClipboardKey = "app.modules.clipboard_enabled";
    public const string LauncherKey  = "app.modules.launcher_enabled";
    public const string WormholesKey = "app.modules.wormholes_enabled";
    public const string KeySequencesKey = "app.modules.keysequences_enabled";
}
