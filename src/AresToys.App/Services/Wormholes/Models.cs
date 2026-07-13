using System.Text.Json.Serialization;

namespace AresToys.App.Services.Wormholes;

/// <summary>Runtime visibility state for a wormhole window. Not persisted — recalculated on
/// every startup + every <c>WM_DISPLAYCHANGE</c>. <see cref="WormholeRecord.IsHidden"/> (which
/// IS persisted) takes precedence: an <c>IsHidden=true</c> wormhole stays <see cref="UserHidden"/>
/// regardless of monitor state.</summary>
public enum HibernationState
{
    Active,
    MonitorOffline,
    UserHidden,
}

/// <summary>Window position, size, and rolled-state pivot. <see cref="UnrolledHeight"/> stores
/// the height to restore to when the user unrolls a previously rolled-up wormhole — without it
/// every unroll would default to a single hard-coded value and lose the user's chosen height.
/// <see cref="MonitorId"/> is best-effort (Windows display device name like
/// <c>\\.\DISPLAY1</c>) and used to decide hibernate vs. resurface on display-change events.</summary>
public sealed class WormholeGeometry
{
    public double X { get; set; } = 200;
    public double Y { get; set; } = 200;
    public double Width { get; set; } = 320;
    public double Height { get; set; } = 240;
    public double UnrolledHeight { get; set; } = 240;
    public string? MonitorId { get; set; }
}

/// <summary>Configuration for the folder a wormhole mirrors. Every wormhole is a folder mirror
/// now — the old "Shortcuts" variant was dropped as it boiled down to a folder mirror pointing
/// at our own hidden Shortcuts\{guid}\ directory.</summary>
public sealed class PortalWormholeConfig
{
    public string SourcePath { get; set; } = string.Empty;
    public bool IncludeSubdirectoriesAsItems { get; set; } = true;
    /// <summary>Sort mode label as a string (not an enum) so future modes can be added without
    /// breaking the persisted schema. Known values: <c>Name</c>, <c>Modified</c>, <c>Type</c>.
    /// Unknown values fall back to <c>Name</c> at load time.</summary>
    public string SortMode { get; set; } = "Name";
}

/// <summary>Per-wormhole appearance overrides. Anything that's nullable / 0-sentinel means
/// "fall back to the app-wide default" (see <see cref="WormholeDefaultsService"/>). Per-record
/// overrides win when present.</summary>
public sealed class WormholeAppearance
{
    /// <summary>Reserved for the future "per-wormhole accent" feature: hex string like
    /// <c>#80E1A0</c>. Null = use the global accent.</summary>
    public string? AccentOverride { get; set; }

    /// <summary>Per-wormhole opacity override. <c>null</c> = use the app-wide default from
    /// <see cref="WormholeDefaultsService.DefaultOpacity"/>. Set via the chrome's appearance
    /// slider; persisted through <see cref="IWormholeStore.SaveAsync"/>.</summary>
    public double? OpacityOverride { get; set; }
}

/// <summary>Single wormhole record as persisted to <c>wormholes.json</c>. Lifecycle is owned by
/// <see cref="IWormholeStore"/> + <see cref="WormholeWindowManager"/>: the store hydrates the
/// list at startup, the manager spawns a window per record, and mutations from the UI flow back
/// through the store's <c>SaveAsync</c>.</summary>
public sealed class WormholeRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "Wormhole";
    public WormholeGeometry Geometry { get; set; } = new();
    public bool IsLocked { get; set; }
    public bool IsRolled { get; set; }
    public bool IsHidden { get; set; }
    /// <summary>True ⇒ this wormhole's window stays above other apps (WPF
    /// <see cref="System.Windows.Window.Topmost"/>). Lets the user pull all wormholes to the
    /// foreground via the Toggle Topmost hotkey without minimising or alt-tabbing the active
    /// app — they reappear over any covering window. Defaults false so existing wormholes
    /// keep the original z-order behaviour.</summary>
    public bool IsTopmost { get; set; }
    /// <summary>Per-wormhole icon-tile pixel size. 0 = "not set, use the system desktop icon
    /// size at render time" (see <see cref="DesktopIconSize"/>). Any other value is the exact
    /// pixel size the user dialed in via Ctrl+MouseWheel inside the wormhole. Persisted so the
    /// next launch reopens at the same zoom. JSON-default 0 means existing pre-zoom wormholes
    /// automatically pick up the desktop size without a migration step.</summary>
    public int IconSizePx { get; set; }
    public WormholeAppearance Appearance { get; set; } = new();
    public PortalWormholeConfig Portal { get; set; } = new();
}

/// <summary>Top-level container of <c>wormholes.json</c>. Carries an explicit
/// <see cref="SchemaVersion"/> so future-format migrations (additive only, per the project
/// convention — see <c>Migration002</c>/<c>Migration003</c> in Storage) can detect older files
/// without parsing the body twice. From v2 the per-record <c>Geometry</c> is no longer
/// serialized into this file: live geometry lives in <c>positions.json</c> and named layout
/// snapshots in <c>presets.json</c> (see <see cref="WormholePositionsFile"/> /
/// <see cref="WormholePresetsFile"/>).</summary>
public sealed class WormholeStoreFile
{
    [JsonPropertyName("$schema_version")]
    public int SchemaVersion { get; set; } = 1;
    public List<WormholeRecord> Wormholes { get; set; } = new();
}

/// <summary>Persisted contents of <c>positions.json</c>: the current live geometry of every
/// wormhole, keyed by <see cref="WormholeRecord.Id"/>. This is the single source of truth for
/// "where the wormholes are right now"; it is written whenever the user moves/resizes a
/// wormhole and read back to restore exact positions on a plain restart. There is no per-monitor
/// splitting and no clamping — a layout is never squashed to fit a smaller screen. Cross-setup
/// layout management is done explicitly through named presets (see <see cref="WormholePreset"/>).</summary>
public sealed class WormholePositionsFile
{
    [JsonPropertyName("$schema_version")]
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<Guid, WormholeGeometry> Positions { get; set; } = new();
}

/// <summary>Per-wormhole visual state captured in a preset alongside geometry: whether the
/// wormhole is hidden, locked, and rolled up. Lets a preset restore "these wormholes are hidden
/// on this setup, those are visible". Topmost is intentionally NOT captured — it is a global
/// bring-to-front mode, not part of a spatial layout.</summary>
public sealed class WormholePresetState
{
    public bool Hidden { get; set; }
    public bool Locked { get; set; }
    public bool Rolled { get; set; }
}

/// <summary>A named layout preset, persisted as its own file under <c>Presets\</c> (one JSON per
/// preset, so the user can delete or hand-edit them in Explorer). Captures the geometry AND
/// per-wormhole visual state (hidden / locked / rolled) of every wormhole at save time, plus the
/// list of monitor fingerprints this preset auto-applies for. The <see cref="Positions"/> and
/// <see cref="States"/> keys match the corresponding <see cref="WormholeRecord.Id"/>.
/// <see cref="States"/> is empty on presets saved before state capture; restoring such a preset
/// leaves hidden/locked/rolled as-is. <see cref="Name"/> is authoritative (the filename is a
/// sanitized convenience). A fingerprint present in no preset's <see cref="Setups"/> is an
/// "unknown" setup (typically RDP): nothing auto-applies, the live layout is left untouched.</summary>
public sealed class WormholePreset
{
    [JsonPropertyName("$schema_version")]
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = string.Empty;
    /// <summary>Monitor fingerprints (<see cref="MonitorSetupIdentifier"/> hashes) this preset
    /// auto-reapplies for. Each fingerprint belongs to at most one preset.</summary>
    public List<string> Setups { get; set; } = new();
    public Dictionary<Guid, WormholeGeometry> Positions { get; set; } = new();
    public Dictionary<Guid, WormholePresetState> States { get; set; } = new();
}
