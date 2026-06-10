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
/// serialized into this file — it lives in <c>Positions\&lt;setupHash&gt;.json</c> so a
/// different monitor configuration (RDP single-screen, plug-in external display) can have its
/// own layout without rewriting the original setup's positions every time.</summary>
public sealed class WormholeStoreFile
{
    [JsonPropertyName("$schema_version")]
    public int SchemaVersion { get; set; } = 1;
    public List<WormholeRecord> Wormholes { get; set; } = new();
}

/// <summary>Persisted contents of a <c>Positions\&lt;setupHash&gt;.json</c> file: per-wormhole
/// geometry pinned to one specific monitor configuration. The hash key in
/// <see cref="Positions"/> matches the corresponding <see cref="WormholeRecord.Id"/>.
/// <para>The "original" setup (first one detected after the per-setup feature lands; tracked
/// via the <c>Positions\.original</c> marker) acts as the template for newly-encountered
/// setups: its positions are clamped into the new virtual screen and written as a separate
/// file. The original's file is never mutated by these clones — when the user reconnects the
/// home monitor, the saved layout there is exactly as they left it.</para></summary>
public sealed class WormholePositionsFile
{
    [JsonPropertyName("$schema_version")]
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<Guid, WormholeGeometry> Positions { get; set; } = new();
}
