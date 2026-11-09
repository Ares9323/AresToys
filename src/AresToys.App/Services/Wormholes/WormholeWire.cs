using System.Globalization;
using System.Text.Json;
using AresToys.App.Services.Satellite;

namespace AresToys.App.Services.Wormholes;

/// <summary>The wire contract between the main app and the Wormholes running in the non elevated satellite
/// (<see cref="WormholesSatelliteModule"/>). Both sides use these names and records, nothing else. The main side is the
/// proxy that implements <see cref="IWormholeWindowManager"/> over the channel (piece D2) and the Settings view models
/// on DTO rows (piece D3).
///
/// <para>Rules of the contract.
/// <list type="bullet">
/// <item>The satellite is the only writer of <c>wormholes.json</c>, <c>positions.json</c>, <c>groups.json</c>, the
/// Presets and Shortcuts folders and of the <c>app.wormholes.*</c> settings. The main app never holds a record: it
/// holds <see cref="RowDto"/> copies and edits them with <see cref="PatchRequest"/>.</item>
/// <item>No live object crosses the wire (no <see cref="WormholeRecord"/>, no window, no handle owner). Payloads are
/// JSON objects, built and read with <see cref="ToElement{T}"/> and <see cref="Read{T}"/>.</item>
/// <item>Every request handler runs on the satellite UI thread and, except <see cref="ReadyEvent"/>, waits until the
/// manager finished <see cref="IWormholeWindowManager.InitializeAsync"/>. A request that arrives during that window
/// may time out on the caller: the caller refreshes when <see cref="ReadyEvent"/> arrives. A failing handler answers
/// with Ok false and a short error text; <c>SatelliteChannel.RequestAsync</c> returns null when the channel is absent
/// or the timeout elapsed, so a proxy method logs and returns a default, never throws or blocks the UI thread.</item>
/// <item>Timeouts are the caller's choice: <see cref="LongTimeoutMs"/> for batch, restore, create and merge,
/// <see cref="DefaultTimeoutMs"/> for everything else.</item>
/// <item>Commands are fire and forget: the main app sends them with <c>SatelliteChannel.Publish</c> and the satellite
/// answers nothing. Events go the other way, the satellite publishes and the main app receives them through
/// <c>EventReceived</c> (on its own task, in order, off the UI thread).</item>
/// </list></para>
///
/// <para>Mapping of <see cref="IWormholeWindowManager"/> members (main side to wire):
/// <list type="table">
/// <item><term><c>InitializeAsync</c></term><description>no wire call: the satellite initialises itself when it starts;
/// the proxy completes it when <see cref="ReadyEvent"/> arrives.</description></item>
/// <item><term><c>CreateAsync</c></term><description><see cref="CreateRequest"/> with <see cref="CreateArgs"/>,
/// answer <see cref="RowDto"/>.</description></item>
/// <item><term><c>DeleteAsync</c></term><description><see cref="DeleteRequest"/> with <see cref="IdArgs"/>.</description></item>
/// <item><term><c>GroupFor</c>, <c>TabsFor</c></term><description>carried by <see cref="RowDto.Group"/> of the rows
/// from <see cref="ListRequest"/>; the tabs are the members of the group.</description></item>
/// <item><term><c>MergeAsync</c>, <c>DetachAsync</c>, <c>SetActiveTabAsync</c></term><description>
/// <see cref="MergeRequest"/> (<see cref="MergeArgs"/>), <see cref="DetachRequest"/> and <see cref="SetActiveTabRequest"/>
/// (<see cref="IdArgs"/>).</description></item>
/// <item><term><c>ShowTabOnHover</c>, <c>HighlightMergeTarget</c>, <c>ClearMergeHighlight</c></term><description>
/// <see cref="ShowTabOnHoverCommand"/> (<see cref="IdArgs"/>), <see cref="HighlightMergeTargetCommand"/>
/// (<see cref="OptionalIdArgs"/>, null clears) and <see cref="ClearMergeHighlightCommand"/>.</description></item>
/// <item><term><c>CloseAll</c>, <c>RecenterAll</c></term><description><see cref="CloseAllCommand"/>,
/// <see cref="RecenterCommand"/>.</description></item>
/// <item><term><c>ReconcileAsync(record)</c></term><description><see cref="PatchRequest"/> with <see cref="PatchArgs"/>
/// (only the fields that are not null are applied, the satellite saves the record and reconciles its window),
/// answer the updated <see cref="RowDto"/>. Moving a row up or down in the list is <see cref="MoveRequest"/>.</description></item>
/// <item><term><c>RecordChanged</c>, <c>RecordDeleted</c>, <c>WormholeFocused</c>, <c>WatchersPausedChanged</c></term>
/// <description><see cref="RecordChangedEvent"/> (a <see cref="RowDto"/>, coalesced to one per record per 250 ms, the
/// latest state), <see cref="RecordDeletedEvent"/>, <see cref="FocusedEvent"/> (both <see cref="IdArgs"/>) and
/// <see cref="WatchersPausedEvent"/> (<see cref="PausedDto"/>).</description></item>
/// <item><term><c>SetAll*Async</c>, <c>ToggleAll*Async</c></term><description><see cref="BatchRequest"/> with
/// <see cref="BatchArgs"/>, the op being one of <see cref="BatchOps"/> (the strings of <c>WormholeBatchOpTask</c>).</description></item>
/// <item><term><c>ListPresetsAsync</c>, <c>CurrentSetupPresetAsync</c>, <c>PresetsFolderPath</c></term><description>
/// <see cref="PresetsListRequest"/>, answer <see cref="PresetListDto"/> (the three values together).</description></item>
/// <item><term><c>SaveCurrentAsPresetAsync</c>, <c>DeletePresetAsync</c>, <c>RenamePresetAsync</c>, <c>RestorePresetAsync</c></term>
/// <description><see cref="PresetsSaveRequest"/>, <see cref="PresetsDeleteRequest"/> (<see cref="NameArgs"/>),
/// <see cref="PresetsRenameRequest"/> (<see cref="RenameArgs"/>), <see cref="PresetsRestoreRequest"/>
/// (<see cref="NameArgs"/>). Each answers when done and publishes <see cref="PresetsChangedEvent"/>.</description></item>
/// <item><term><c>MissingSourcesAsync</c></term><description><see cref="SourcesMissingRequest"/>, answer
/// <see cref="RowDto"/>[].</description></item>
/// <item><term><c>RelinkSource</c>, <c>RelinkMissingSourcesAsync</c></term><description><see cref="SourcesRelinkRequest"/>
/// with <see cref="RelinkArgs"/>, answer <see cref="RelinkResult"/>.</description></item>
/// <item><term><c>RequestSourceRecovery</c></term><description><see cref="RecoverSourceCommand"/> (<see cref="IdArgs"/>).</description></item>
/// <item><term><c>WatchersPaused</c>, <c>PauseWatchers</c>, <c>ResumeWatchers</c></term><description>
/// <see cref="WatchersPauseCommand"/>, <see cref="WatchersResumeCommand"/>; the state is carried by
/// <see cref="WatchersPausedEvent"/> and by <see cref="ReadyDto.Paused"/>, the proxy caches it.</description></item>
/// <item><term><c>LiveWindowHandles</c></term><description><see cref="WindowHandlesRequest"/>, answer
/// <see cref="WindowHandlesDto"/>. The handles belong to the satellite process; the main app only uses them as ids.</description></item>
/// <item><term><c>NotifyItemSelectionTaken</c>, <c>NotifyWormholeFocused</c>, <c>FindHeaderTargetAt</c></term>
/// <description>not on the interface any more: they take WPF types and live on <see cref="IWormholeWindowHost"/>,
/// satellite only.</description></item>
/// </list></para>
///
/// <para>The 20 <c>app.wormholes.*</c> defaults: <see cref="DefaultsGetRequest"/> answers a <see cref="DefaultsDto"/>;
/// <see cref="DefaultsSetRequest"/> takes a <see cref="DefaultsSetArgs"/> whose <c>Key</c> is one of the
/// <c>WormholeDefaultsService.*Key</c> constants and whose <c>Value</c> is the text the setting store would hold
/// (invariant culture; <c>true</c>/<c>false</c> for flags), answers the stored (clamped) <see cref="DefaultsDto"/> and
/// publishes <see cref="DefaultsChangedEvent"/> with it. The main app keeps a read only copy and reloads it on that
/// event.</para></summary>
public static class WormholeWire
{
    // ------------------------------------------------------------------------------------------
    // Requests (main to satellite, the answer carries the result)
    // ------------------------------------------------------------------------------------------

    /// <summary>No payload. Answer: <see cref="RowDto"/>[] in persisted order.</summary>
    public const string ListRequest = "wormholes.list";

    /// <summary><see cref="CreateArgs"/>. Answer: the new <see cref="RowDto"/>. Caller timeout <see cref="LongTimeoutMs"/>.</summary>
    public const string CreateRequest = "wormholes.create";

    /// <summary><see cref="IdArgs"/>. No answer payload.</summary>
    public const string DeleteRequest = "wormholes.delete";

    /// <summary><see cref="MoveArgs"/>. Answer: <see cref="MoveResult"/>. Changes the display order only.</summary>
    public const string MoveRequest = "wormholes.move";

    /// <summary><see cref="PatchArgs"/>. Answer: the updated <see cref="RowDto"/>.</summary>
    public const string PatchRequest = "wormholes.patch";

    /// <summary><see cref="BatchArgs"/>. No answer payload. Caller timeout <see cref="LongTimeoutMs"/>.</summary>
    public const string BatchRequest = "wormholes.batch";

    /// <summary><see cref="MergeArgs"/>. No answer payload. Caller timeout <see cref="LongTimeoutMs"/>.</summary>
    public const string MergeRequest = "wormholes.merge";

    /// <summary><see cref="IdArgs"/>. No answer payload.</summary>
    public const string DetachRequest = "wormholes.detach";

    /// <summary><see cref="IdArgs"/>. No answer payload.</summary>
    public const string SetActiveTabRequest = "wormholes.setActiveTab";

    /// <summary>No payload. Answer: <see cref="PresetListDto"/>.</summary>
    public const string PresetsListRequest = "wormholes.presets.list";

    /// <summary><see cref="NameArgs"/>. No answer payload.</summary>
    public const string PresetsSaveRequest = "wormholes.presets.save";

    /// <summary><see cref="NameArgs"/>. No answer payload.</summary>
    public const string PresetsDeleteRequest = "wormholes.presets.delete";

    /// <summary><see cref="RenameArgs"/>. No answer payload.</summary>
    public const string PresetsRenameRequest = "wormholes.presets.rename";

    /// <summary><see cref="NameArgs"/>. No answer payload. Caller timeout <see cref="LongTimeoutMs"/>.</summary>
    public const string PresetsRestoreRequest = "wormholes.presets.restore";

    /// <summary>No payload. Answer: <see cref="RowDto"/>[] of the records whose source folder does not resolve.</summary>
    public const string SourcesMissingRequest = "wormholes.sources.missing";

    /// <summary><see cref="RelinkArgs"/>. Answer: <see cref="RelinkResult"/>. Caller timeout <see cref="LongTimeoutMs"/>.</summary>
    public const string SourcesRelinkRequest = "wormholes.sources.relink";

    /// <summary>No payload. Answer: <see cref="DefaultsDto"/>.</summary>
    public const string DefaultsGetRequest = "wormholes.defaults.get";

    /// <summary><see cref="DefaultsSetArgs"/>. Answer: the stored <see cref="DefaultsDto"/>.</summary>
    public const string DefaultsSetRequest = "wormholes.defaults.set";

    /// <summary>No payload. Answer: <see cref="WindowHandlesDto"/>.</summary>
    public const string WindowHandlesRequest = "wormholes.windowHandles";

    // ------------------------------------------------------------------------------------------
    // Commands (main to satellite, fire and forget, sent with SatelliteChannel.Publish)
    // ------------------------------------------------------------------------------------------

    /// <summary>No payload. Puts every live wormhole on the primary monitor in a cascade.</summary>
    public const string RecenterCommand = "wormholes.recenter";

    /// <summary>No payload. Releases every folder watcher; <see cref="WatchersPausedEvent"/> follows.</summary>
    public const string WatchersPauseCommand = "wormholes.watchers.pause";

    /// <summary>No payload. Re-attaches the watchers; <see cref="WatchersPausedEvent"/> follows.</summary>
    public const string WatchersResumeCommand = "wormholes.watchers.resume";

    /// <summary>No payload. Closes every wormhole window, the records stay.</summary>
    public const string CloseAllCommand = "wormholes.closeAll";

    /// <summary><see cref="IdArgs"/>. Shows a tab of a collapsed group because the pointer rests on it.</summary>
    public const string ShowTabOnHoverCommand = "wormholes.showTabOnHover";

    /// <summary><see cref="OptionalIdArgs"/>. Lights up the header a dragged wormhole would merge into; no id clears it.</summary>
    public const string HighlightMergeTargetCommand = "wormholes.highlightMergeTarget";

    /// <summary>No payload. Clears any merge cue.</summary>
    public const string ClearMergeHighlightCommand = "wormholes.clearMergeHighlight";

    /// <summary><see cref="IdArgs"/>. Starts the source folder recovery chain for a wormhole whose folder went missing.</summary>
    public const string RecoverSourceCommand = "wormholes.sources.recover";

    // ------------------------------------------------------------------------------------------
    // Events (satellite to main, sent with SatelliteChannel.Publish)
    // ------------------------------------------------------------------------------------------

    /// <summary><see cref="RowDto"/>: the record's latest state. A drag raises it on every move, so the satellite sends
    /// at most one per record per <see cref="RecordChangedCoalesceMs"/> ms.</summary>
    public const string RecordChangedEvent = "wormholes.recordChanged";

    /// <summary><see cref="IdArgs"/>: the record no longer exists.</summary>
    public const string RecordDeletedEvent = "wormholes.recordDeleted";

    /// <summary><see cref="IdArgs"/>: the user interacted with that wormhole (highlight its row).</summary>
    public const string FocusedEvent = "wormholes.focused";

    /// <summary><see cref="PausedDto"/>: the watchers were paused or resumed.</summary>
    public const string WatchersPausedEvent = "wormholes.watchersPaused";

    /// <summary>No payload: the preset list or the preset bound to the current monitor setup may have changed.</summary>
    public const string PresetsChangedEvent = "wormholes.presetsChanged";

    /// <summary><see cref="DefaultsDto"/>: a default changed.</summary>
    public const string DefaultsChangedEvent = "wormholes.defaultsChanged";

    /// <summary><see cref="ReadyDto"/>: the manager finished initialising, every window of a visible record exists.
    /// Sent once per satellite run.</summary>
    public const string ReadyEvent = "wormholes.ready";

    // ------------------------------------------------------------------------------------------
    // Numbers
    // ------------------------------------------------------------------------------------------

    /// <summary>Request timeout for the quick requests.</summary>
    public const int DefaultTimeoutMs = 3000;

    /// <summary>Request timeout for batch, restore, create, merge and relink: they spawn or move many windows.</summary>
    public const int LongTimeoutMs = 15000;

    /// <summary>Trailing window of the <see cref="RecordChangedEvent"/> coalescer.</summary>
    public const int RecordChangedCoalesceMs = 250;

    /// <summary>The <see cref="BatchArgs.Op"/> strings: the same ones the <c>WormholeBatchOpTask</c> workflow step uses
    /// in its config, each mapped to one <see cref="IWormholeWindowManager"/> SetAll or ToggleAll member.</summary>
    public static class BatchOps
    {
        public const string HideAll = "hide-all";
        public const string ShowAll = "show-all";
        public const string LockAll = "lock-all";
        public const string UnlockAll = "unlock-all";
        public const string CollapseAll = "collapse-all";
        public const string UncollapseAll = "uncollapse-all";
        public const string TopmostAll = "topmost-all";
        public const string UntopmostAll = "untopmost-all";
        public const string ToggleHide = "toggle-hide";
        public const string ToggleLock = "toggle-lock";
        public const string ToggleCollapse = "toggle-collapse";
        public const string ToggleTopmost = "toggle-topmost";
    }

    // ------------------------------------------------------------------------------------------
    // Payloads
    // ------------------------------------------------------------------------------------------

    /// <summary>A wormhole's window rectangle in DIPs, as persisted in <c>positions.json</c>.</summary>
    public sealed record GeometryDto(double X, double Y, double Width, double Height, double UnrolledHeight, string? MonitorId);

    /// <summary>The tab group a wormhole belongs to. <see cref="Members"/> are the tabs in display order, the same
    /// list for every member.</summary>
    public sealed record GroupDto(Guid ParentId, Guid ActiveId, Guid[] Members);

    /// <summary>One wormhole as the Settings rows and the tray use it: a copy, never the satellite's own record.
    /// <see cref="Group"/> is null for a wormhole that stands alone.</summary>
    public sealed record RowDto(
        Guid Id,
        string Title,
        GeometryDto Geometry,
        bool IsLocked,
        bool IsRolled,
        bool IsHidden,
        bool IsTopmost,
        int IconSizePx,
        string SourcePath,
        bool IncludeSubdirectoriesAsItems,
        string SortMode,
        string? AccentOverride,
        double? OpacityOverride,
        GroupDto? Group)
    {
        /// <summary>Copies a record (and the group it belongs to) into a row. Satellite side.</summary>
        public static RowDto From(WormholeRecord record, WormholeGroup? group)
        {
            ArgumentNullException.ThrowIfNull(record);
            var g = record.Geometry;
            var portal = record.Portal;
            return new RowDto(
                record.Id,
                record.Title,
                new GeometryDto(g.X, g.Y, g.Width, g.Height, g.UnrolledHeight, g.MonitorId),
                record.IsLocked,
                record.IsRolled,
                record.IsHidden,
                record.IsTopmost,
                record.IconSizePx,
                portal?.SourcePath ?? string.Empty,
                portal?.IncludeSubdirectoriesAsItems ?? true,
                portal?.SortMode ?? "Name",
                record.Appearance?.AccentOverride,
                record.Appearance?.OpacityOverride,
                group is null ? null : new GroupDto(group.ParentId, group.ActiveId, group.Members.ToArray()));
        }
    }

    /// <summary>Request payload that names one wormhole.</summary>
    public sealed record IdArgs(Guid Id);

    /// <summary>Request payload that names one wormhole or none.</summary>
    public sealed record OptionalIdArgs(Guid? Id);

    /// <summary><see cref="CreateRequest"/>. The caller has checked that the folder exists. A blank title becomes "Wormhole".</summary>
    public sealed record CreateArgs(string? Title, string SourcePath);

    /// <summary><see cref="MoveRequest"/>: -1 moves the row up, +1 down, clamped at the ends.</summary>
    public sealed record MoveArgs(Guid Id, int Delta);

    /// <summary>The row's new index, or -1 when the id is unknown or <c>Delta</c> is 0.</summary>
    public sealed record MoveResult(int Index);

    /// <summary><see cref="PatchRequest"/>: the fields to change on the record with <see cref="Id"/>; null means leave
    /// as is. This is what a Settings row edit sends (lock, hidden, X, Y, W, H) and what replaces
    /// <c>ReconcileAsync(record)</c>.</summary>
    public sealed record PatchArgs(
        Guid Id,
        string? Title = null,
        bool? IsLocked = null,
        bool? IsRolled = null,
        bool? IsHidden = null,
        bool? IsTopmost = null,
        int? IconSizePx = null,
        double? X = null,
        double? Y = null,
        double? Width = null,
        double? Height = null,
        double? UnrolledHeight = null)
    {
        /// <summary>Smallest width a patch can set, the same floor the Settings row applies.</summary>
        public const double MinWidth = 80;

        /// <summary>Smallest height a patch can set, the same floor the Settings row applies.</summary>
        public const double MinHeight = 40;

        /// <summary>Applies the non null fields to the satellite's own record. A new <see cref="Height"/> on a record
        /// that is not rolled also becomes its <see cref="UnrolledHeight"/> (unless one is sent), so a later unroll
        /// restores it. Satellite side.</summary>
        public void ApplyTo(WormholeRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);
            if (Title is not null) record.Title = string.IsNullOrWhiteSpace(Title) ? "Wormhole" : Title;
            if (IsLocked is { } locked) record.IsLocked = locked;
            if (IsRolled is { } rolled) record.IsRolled = rolled;
            if (IsHidden is { } hidden) record.IsHidden = hidden;
            if (IsTopmost is { } topmost) record.IsTopmost = topmost;
            if (IconSizePx is { } icon) record.IconSizePx = Math.Max(0, icon);
            if (X is { } x) record.Geometry.X = x;
            if (Y is { } y) record.Geometry.Y = y;
            if (Width is { } w) record.Geometry.Width = Math.Max(MinWidth, w);
            if (Height is { } h)
            {
                record.Geometry.Height = Math.Max(MinHeight, h);
                if (!record.IsRolled && UnrolledHeight is null) record.Geometry.UnrolledHeight = record.Geometry.Height;
            }
            if (UnrolledHeight is { } unrolled) record.Geometry.UnrolledHeight = Math.Max(MinHeight, unrolled);
        }
    }

    /// <summary><see cref="BatchRequest"/>: one of <see cref="BatchOps"/>. An unknown op answers an error.</summary>
    public sealed record BatchArgs(string Op);

    /// <summary><see cref="MergeRequest"/>: fold <see cref="Dragged"/> into the window hosting <see cref="Target"/>.</summary>
    public sealed record MergeArgs(Guid Dragged, Guid Target);

    /// <summary>Request payload that names a preset.</summary>
    public sealed record NameArgs(string Name);

    /// <summary><see cref="PresetsRenameRequest"/>.</summary>
    public sealed record RenameArgs(string OldName, string NewName);

    /// <summary><see cref="PresetsListRequest"/> answer: the preset names in insertion order, the preset bound to the
    /// current monitor setup (null when this setup is unknown) and the absolute path of the folder that holds one JSON
    /// per preset (what <c>PresetsFolderPath</c> returns).</summary>
    public sealed record PresetListDto(string[] Names, string? CurrentSetup, string FolderPath);

    /// <summary><see cref="SourcesRelinkRequest"/>: with <see cref="Id"/> points that wormhole at <see cref="Path"/>;
    /// without it re-roots every missing source under the base folder <see cref="Path"/>.</summary>
    public sealed record RelinkArgs(Guid? Id, string Path);

    /// <summary>How many sources were repaired.</summary>
    public sealed record RelinkResult(int Count);

    /// <summary><see cref="DefaultsSetRequest"/>.</summary>
    public sealed record DefaultsSetArgs(string Key, string Value);

    /// <summary><see cref="WatchersPausedEvent"/> payload.</summary>
    public sealed record PausedDto(bool Paused);

    /// <summary><see cref="ReadyEvent"/> payload: the watcher state at that moment.</summary>
    public sealed record ReadyDto(bool Paused);

    /// <summary><see cref="WindowHandlesRequest"/> answer: window handles of the satellite process.</summary>
    public sealed record WindowHandlesDto(long[] Handles);

    /// <summary>The 20 <c>app.wormholes.*</c> settings, one property each. The property order follows the keys in
    /// <see cref="WormholeDefaultsService"/>.</summary>
    public sealed record DefaultsDto(
        int IconSizePx,
        double Opacity,
        double BorderOpacity,
        int TilePaddingPx,
        int LineSpacingPx,
        int LabelFontSizePx,
        int LabelMaxLines,
        bool AutoDisableTopmostOnLaunch,
        bool WebLinkFavicons,
        bool KeepVisibleOnShowDesktop,
        bool ExpandCollapsedOnHover,
        bool OpenWithOneClick,
        bool HideServiceFiles,
        bool ShortcutArrowOverlay,
        bool HideFileExtensions,
        bool SnapToGrid,
        int SnapGridSizePx,
        bool SnapToWormholes,
        bool SnapToScreenEdges,
        int SnapGapPx)
    {
        /// <summary>Reads the service's current values. Satellite side.</summary>
        public static DefaultsDto From(WormholeDefaultsService d)
        {
            ArgumentNullException.ThrowIfNull(d);
            return new DefaultsDto(
                d.DefaultIconSizePx, d.DefaultOpacity, d.DefaultBorderOpacity, d.DefaultTilePaddingPx, d.DefaultLineSpacingPx,
                d.DefaultLabelFontSizePx, d.DefaultLabelMaxLines, d.AutoDisableTopmostOnLaunch, d.WebLinkFaviconsEnabled,
                d.KeepVisibleOnShowDesktop, d.ExpandCollapsedOnHover, d.OpenWithOneClick, d.HideServiceFiles,
                d.ShortcutArrowOverlay, d.HideFileExtensions, d.SnapToGrid, d.SnapGridSizePx, d.SnapToWormholes,
                d.SnapToScreenEdges, d.SnapGapPx);
        }
    }

    /// <summary>Writes one default through the service (which clamps it, saves it and raises its change event so the
    /// live windows refresh). Satellite side, on the UI thread. Throws <see cref="ArgumentException"/> for an unknown
    /// key or a value that does not parse.</summary>
    public static Task SetDefaultAsync(WormholeDefaultsService d, string key, string value, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        return key switch
        {
            WormholeDefaultsService.IconSizeKey => d.SetDefaultIconSizeAsync(ParseInt(key, value), ct),
            WormholeDefaultsService.OpacityKey => d.SetDefaultOpacityAsync(ParseDouble(key, value), ct),
            WormholeDefaultsService.BorderOpacityKey => d.SetDefaultBorderOpacityAsync(ParseDouble(key, value), ct),
            WormholeDefaultsService.TilePaddingKey => d.SetDefaultTilePaddingAsync(ParseInt(key, value), ct),
            WormholeDefaultsService.LineSpacingKey => d.SetDefaultLineSpacingAsync(ParseInt(key, value), ct),
            WormholeDefaultsService.LabelFontSizeKey => d.SetDefaultLabelFontSizeAsync(ParseInt(key, value), ct),
            WormholeDefaultsService.LabelMaxLinesKey => d.SetDefaultLabelMaxLinesAsync(ParseInt(key, value), ct),
            WormholeDefaultsService.AutoDisableTopmostOnLaunchKey => d.SetAutoDisableTopmostOnLaunchAsync(ParseBool(key, value), ct),
            WormholeDefaultsService.WebLinkFaviconsKey => d.SetWebLinkFaviconsEnabledAsync(ParseBool(key, value), ct),
            WormholeDefaultsService.KeepVisibleOnShowDesktopKey => d.SetKeepVisibleOnShowDesktopAsync(ParseBool(key, value), ct),
            WormholeDefaultsService.ExpandCollapsedOnHoverKey => d.SetExpandCollapsedOnHoverAsync(ParseBool(key, value), ct),
            WormholeDefaultsService.OpenWithOneClickKey => d.SetOpenWithOneClickAsync(ParseBool(key, value), ct),
            WormholeDefaultsService.HideServiceFilesKey => d.SetHideServiceFilesAsync(ParseBool(key, value), ct),
            WormholeDefaultsService.ShortcutArrowOverlayKey => d.SetShortcutArrowOverlayAsync(ParseBool(key, value), ct),
            WormholeDefaultsService.HideFileExtensionsKey => d.SetHideFileExtensionsAsync(ParseBool(key, value), ct),
            WormholeDefaultsService.SnapToGridKey => d.SetSnapToGridAsync(ParseBool(key, value), ct),
            WormholeDefaultsService.SnapGridSizeKey => d.SetSnapGridSizeAsync(ParseInt(key, value), ct),
            WormholeDefaultsService.SnapToWormholesKey => d.SetSnapToWormholesAsync(ParseBool(key, value), ct),
            WormholeDefaultsService.SnapToScreenEdgesKey => d.SetSnapToScreenEdgesAsync(ParseBool(key, value), ct),
            WormholeDefaultsService.SnapGapKey => d.SetSnapGapAsync(ParseInt(key, value), ct),
            _ => throw new ArgumentException($"Unknown wormhole default '{key}'."),
        };
    }

    private static int ParseInt(string key, string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new ArgumentException($"'{value}' is not a whole number for '{key}'.");

    private static double ParseDouble(string key, string value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new ArgumentException($"'{value}' is not a number for '{key}'.");

    private static bool ParseBool(string key, string value)
        => bool.TryParse(value, out var b)
            ? b
            : throw new ArgumentException($"'{value}' is not true or false for '{key}'.");

    // ------------------------------------------------------------------------------------------
    // Serialisation helpers, shared by both sides so the options never drift apart
    // ------------------------------------------------------------------------------------------

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Builds a payload with the channel's own JSON options (camel case, nulls left out).</summary>
    public static JsonElement ToElement<T>(T value) => SatelliteProtocol.ToElement(value);

    /// <summary>Reads a payload. Default when there is none, it is JSON null or it does not fit <typeparamref name="T"/>.</summary>
    public static T? Read<T>(JsonElement? data)
    {
        if (data is not { } element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return default;
        try { return element.Deserialize<T>(ReadOptions); }
        catch (JsonException) { return default; }
    }

    /// <summary>Reads the payload of an answer. Default when the answer is null (channel closed, timeout), failed
    /// (<c>Ok</c> false) or has no payload.</summary>
    public static T? ReadAnswer<T>(SatelliteMessage? answer)
        => answer is { Ok: true } ? Read<T>(answer.Data) : default;
}
