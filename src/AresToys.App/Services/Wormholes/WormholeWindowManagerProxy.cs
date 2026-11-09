using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.Logging;
using AresToys.App.Services.Satellite;
using AresToys.Storage.Paths;

namespace AresToys.App.Services.Wormholes;

/// <summary>The main app side of the Wormholes. The windows, the store, the folder watchers and the 20 defaults live in
/// the non elevated satellite (<see cref="WormholesSatelliteModule"/>); this class implements
/// <see cref="IWormholeWindowManager"/> by talking to it over the satellite channel with the names and payloads of
/// <see cref="WormholeWire"/>. Every consumer in the main app (workflow tasks, tray, Settings, the Explorer verbs) keeps
/// using the interface.
/// <para>Rules. No call blocks the calling thread and none throws because the satellite is absent: a request that gets no
/// answer (no channel, timeout, a refusal) is logged and the method returns its default. The one deliberate exception is
/// <see cref="RenamePresetAsync"/>, which throws <see cref="ArgumentException"/> with the satellite's text when it refuses
/// the new name, because the Settings panel shows that text. Commands (recenter, close all, watchers, merge cues) are fire
/// and forget.</para>
/// <para>Caches. The UI thread reads some of this synchronously (tray presets, the workflow preset dropdown, the
/// watchers flag, the defaults), so the proxy keeps copies: the rows (<see cref="RowDto"/> in persisted order), the
/// presets with the folder and the current setup preset, the defaults and the watchers flag. They are filled when the
/// satellite sends <see cref="WormholeWire.ReadyEvent"/> (and again when it reconnects) and kept current by its events.
/// What the interface hands out are copies built from them, never the satellite's own objects.</para>
/// <para>Events (<see cref="RecordChanged"/> and the others) are raised on the UI dispatcher, in the order the satellite
/// sent them. Singleton in the main container, created before <see cref="SatelliteService.Apply"/> so no event is missed.</para></summary>
public sealed class WormholeWindowManagerProxy : IWormholeWindowManager, IWormholeRowSource, IDisposable
{
    /// <summary>The longest a call waits for the satellite to connect before it gives up.</summary>
    private static readonly TimeSpan ChannelWait = TimeSpan.FromSeconds(10);

    // Same folder names as WormholeStoreJson (private there). Only used until the satellite has told the real path.
    private const string WormholesFolderName = "Wormholes";
    private const string PresetsFolderName = "Presets";

    /// <summary>The values <c>WormholeDefaultsService</c> holds before it has loaded anything.</summary>
    public static readonly WormholeWire.DefaultsDto BuiltInDefaults = new(
        IconSizePx: 0, Opacity: 0.70, BorderOpacity: 1.00, TilePaddingPx: 4, LineSpacingPx: -4, LabelFontSizePx: 12,
        LabelMaxLines: 2, AutoDisableTopmostOnLaunch: true, WebLinkFavicons: true, KeepVisibleOnShowDesktop: true,
        ExpandCollapsedOnHover: false, OpenWithOneClick: false, HideServiceFiles: true, ShortcutArrowOverlay: true,
        HideFileExtensions: false, SnapToGrid: false, SnapGridSizePx: 16, SnapToWormholes: false,
        SnapToScreenEdges: false, SnapGapPx: 0);

    private readonly SatelliteService _satellite;
    private readonly IStoragePathResolver _paths;
    private readonly ILogger<WormholeWindowManagerProxy> _logger;
    private readonly object _gate = new();
    // Guarded by _gate.
    private readonly List<WormholeWire.RowDto> _rows = [];
    private SatelliteChannel? _subscribed;
    // Replaced (not reset) when the satellite goes away or comes back, so a waiter keeps a valid task.
    private TaskCompletionSource _ready = NewReady();
    private volatile WormholeWire.PresetListDto? _presets;
    private volatile WormholeWire.DefaultsDto _defaults = BuiltInDefaults;
    private volatile bool _watchersPaused;
    private volatile long[] _handles = [];
    private int _disposed;

    public WormholeWindowManagerProxy(SatelliteService satellite, IStoragePathResolver paths, ILogger<WormholeWindowManagerProxy> logger)
    {
        _satellite = satellite;
        _paths = paths;
        _logger = logger;
        _satellite.Server.Connected += OnConnected;
        // A satellite that connected before this proxy existed has no subscription yet. Its ready event may be gone, so
        // ask once now; the satellite answers when it is ready, or the request times out and the event refreshes later.
        if (_satellite.Server.Current is { } existing)
        {
            OnConnected(existing);
            _ = RefreshAllAsync();
        }
    }

    private static TaskCompletionSource NewReady() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ------------------------------------------------------------------------------------------
    // Lifecycle: connection, ready, events
    // ------------------------------------------------------------------------------------------

    /// <summary>True once the satellite said its wormholes are ready (all windows exist) and the connection is still up.</summary>
    public bool IsReady => Volatile.Read(ref _ready).Task.IsCompleted;

    /// <summary>Completes when <c>wormholes.ready</c> has arrived. False after <paramref name="timeout"/> or when
    /// <paramref name="cancellationToken"/> is cancelled. Never throws, never blocks the thread.</summary>
    public async Task<bool> WaitReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var task = Volatile.Read(ref _ready).Task;
        if (task.IsCompleted) return true;
        try
        {
            await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }

    private void OnConnected(SatelliteChannel channel)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_gate)
        {
            if (ReferenceEquals(_subscribed, channel)) return;
            if (_subscribed is { } old) old.EventReceived -= OnEvent;
            _subscribed = channel;
            // A new satellite has not finished initialising yet.
            if (Volatile.Read(ref _ready).Task.IsCompleted) Volatile.Write(ref _ready, NewReady());
        }
        channel.EventReceived += OnEvent;
        channel.Closed += () => OnChannelClosed(channel);
    }

    private void OnChannelClosed(SatelliteChannel channel)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_subscribed, channel)) return;
            _subscribed = null;
            if (Volatile.Read(ref _ready).Task.IsCompleted) Volatile.Write(ref _ready, NewReady());
        }
        channel.EventReceived -= OnEvent;
        _logger.LogDebug("Wormholes: the satellite channel closed");
    }

    /// <summary>Runs on the channel's own event task, in order, off the UI thread.</summary>
    private void OnEvent(string name, JsonElement? data)
    {
        if (!name.StartsWith("wormholes.", StringComparison.Ordinal) || Volatile.Read(ref _disposed) != 0) return;
        try
        {
            switch (name)
            {
                case WormholeWire.ReadyEvent:
                    OnReady(WormholeWire.Read<WormholeWire.ReadyDto>(data));
                    break;
                case WormholeWire.RecordChangedEvent:
                    if (WormholeWire.Read<WormholeWire.RowDto>(data) is { } row)
                    {
                        Upsert(row);
                        RaiseOnUi(() => RecordChanged?.Invoke(this, row.Id));
                    }
                    break;
                case WormholeWire.RecordDeletedEvent:
                    if (WormholeWire.Read<WormholeWire.IdArgs>(data) is { } deleted)
                    {
                        Remove(deleted.Id);
                        RaiseOnUi(() => RecordDeleted?.Invoke(this, deleted.Id));
                    }
                    break;
                case WormholeWire.FocusedEvent:
                    if (WormholeWire.Read<WormholeWire.IdArgs>(data) is { } focused)
                        RaiseOnUi(() => WormholeFocused?.Invoke(this, focused.Id));
                    break;
                case WormholeWire.WatchersPausedEvent:
                    SetWatchersPaused(WormholeWire.Read<WormholeWire.PausedDto>(data)?.Paused ?? false);
                    break;
                case WormholeWire.PresetsChangedEvent:
                    _ = RefreshPresetsAsync();
                    break;
                case WormholeWire.DefaultsChangedEvent:
                    if (WormholeWire.Read<WormholeWire.DefaultsDto>(data) is { } defaults) _defaults = defaults;
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Wormholes: handling the event '{Name}' failed", name);
        }
    }

    private void OnReady(WormholeWire.ReadyDto? ready)
    {
        SetWatchersPaused(ready?.Paused ?? false);
        Volatile.Read(ref _ready).TrySetResult();
        // Anything asked before this point may have timed out while the satellite was starting.
        _ = RefreshAllAsync();
    }

    private void SetWatchersPaused(bool paused)
    {
        if (_watchersPaused == paused) return;
        _watchersPaused = paused;
        RaiseOnUi(() => WatchersPausedChanged?.Invoke(this, EventArgs.Empty));
    }

    private void RaiseOnUi(Action raise)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(() =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try { raise(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Wormholes: an event subscriber failed"); }
        });
    }

    // ------------------------------------------------------------------------------------------
    // Requests and commands
    // ------------------------------------------------------------------------------------------

    /// <summary>Sends a request. Null when there is no channel, nobody answered in time, or the satellite refused (the
    /// reason is in the log). Never throws.</summary>
    private async Task<SatelliteMessage?> SendAsync(string name, JsonElement? data = null, int timeoutMs = WormholeWire.DefaultTimeoutMs)
    {
        if (Volatile.Read(ref _disposed) != 0) return null;
        var channel = _satellite.Server.Current;
        if (channel is null)
        {
            _logger.LogDebug("Wormholes: no satellite connected, '{Name}' skipped", name);
            return null;
        }

        try
        {
            var answer = await channel.RequestAsync(name, data, timeoutMs).ConfigureAwait(false);
            if (answer is null)
            {
                _logger.LogWarning("Wormholes: no answer to '{Name}' (closed channel or timeout)", name);
                return null;
            }
            if (!answer.Ok)
            {
                _logger.LogWarning("Wormholes: the satellite refused '{Name}': {Error}", name, answer.Error);
                return null;
            }
            return answer;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Wormholes: request '{Name}' failed", name);
            return null;
        }
    }

    /// <summary>Like <see cref="SendAsync"/> but a refusal comes back as the satellite's text instead of being swallowed.</summary>
    private async Task<(SatelliteMessage? Answer, string? Refusal)> SendKeepingRefusalAsync(string name, JsonElement? data)
    {
        var channel = _satellite.Server.Current;
        if (channel is null || Volatile.Read(ref _disposed) != 0) return (null, null);
        try
        {
            var answer = await channel.RequestAsync(name, data, WormholeWire.DefaultTimeoutMs).ConfigureAwait(false);
            if (answer is null)
            {
                _logger.LogWarning("Wormholes: no answer to '{Name}' (closed channel or timeout)", name);
                return (null, null);
            }
            return answer.Ok ? (answer, null) : (null, answer.Error ?? "The satellite refused the request.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Wormholes: request '{Name}' failed", name);
            return (null, null);
        }
    }

    private void SendCommand(string name, JsonElement? data = null)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var channel = _satellite.Server.Current;
        if (channel is null || !channel.Publish(name, data))
            _logger.LogDebug("Wormholes: no satellite connected, command '{Name}' dropped", name);
    }

    private async Task<bool> WaitForChannelAsync(CancellationToken cancellationToken)
    {
        var until = DateTime.UtcNow + ChannelWait;
        while (_satellite.Server.Current is null)
        {
            if (DateTime.UtcNow >= until || Volatile.Read(ref _disposed) != 0) return false;
            try { await Task.Delay(100, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }
        return true;
    }

    // ------------------------------------------------------------------------------------------
    // Caches
    // ------------------------------------------------------------------------------------------

    private async Task RefreshAllAsync()
    {
        try
        {
            await RefreshRowsAsync().ConfigureAwait(false);
            await RefreshPresetsAsync().ConfigureAwait(false);
            await LoadDefaultsAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Wormholes: refreshing the caches failed");
        }
    }

    /// <summary>Replaces the row cache with the satellite's list. Returns the rows now cached.</summary>
    private async Task<WormholeWire.RowDto[]> RefreshRowsAsync()
    {
        var answer = await SendAsync(WormholeWire.ListRequest).ConfigureAwait(false);
        if (WormholeWire.ReadAnswer<WormholeWire.RowDto[]>(answer) is { } rows)
        {
            lock (_gate)
            {
                _rows.Clear();
                _rows.AddRange(rows);
            }
            return rows;
        }
        lock (_gate) return _rows.ToArray();
    }

    private async Task<WormholeWire.PresetListDto?> RefreshPresetsAsync()
    {
        var answer = await SendAsync(WormholeWire.PresetsListRequest).ConfigureAwait(false);
        if (WormholeWire.ReadAnswer<WormholeWire.PresetListDto>(answer) is { } presets) _presets = presets;
        return _presets;
    }

    private void Upsert(WormholeWire.RowDto row)
    {
        lock (_gate)
        {
            var index = _rows.FindIndex(r => r.Id == row.Id);
            if (index >= 0) _rows[index] = row; else _rows.Add(row);
        }
    }

    private void Remove(Guid id)
    {
        lock (_gate) _rows.RemoveAll(r => r.Id == id);
    }

    private WormholeWire.RowDto? CachedRow(Guid id)
    {
        lock (_gate) return _rows.FirstOrDefault(r => r.Id == id);
    }

    /// <summary>The preset names the cache holds. No request: safe to read on the UI thread (the tray menu, the workflow
    /// dropdown). Empty until the satellite has answered.</summary>
    public IReadOnlyList<string> CachedPresetNames => _presets?.Names ?? [];

    /// <summary>The preset bound to the current monitor setup as the cache knows it, or null. No request.</summary>
    public string? CachedCurrentSetupPreset => _presets?.CurrentSetup;

    /// <summary>Asks the satellite for the preset list now and refreshes the cache (the tray calls it when its submenu
    /// opens so the next opening is current). Fire and forget.</summary>
    public void RequestPresetsRefresh() => _ = RefreshPresetsAsync();

    // ------------------------------------------------------------------------------------------
    // Mapping rows to the records the interface exposes (always copies)
    // ------------------------------------------------------------------------------------------

    private static WormholeRecord ToRecord(WormholeWire.RowDto row) => new()
    {
        Id = row.Id,
        Title = row.Title,
        Geometry = new WormholeGeometry
        {
            X = row.Geometry.X,
            Y = row.Geometry.Y,
            Width = row.Geometry.Width,
            Height = row.Geometry.Height,
            UnrolledHeight = row.Geometry.UnrolledHeight,
            MonitorId = row.Geometry.MonitorId,
        },
        IsLocked = row.IsLocked,
        IsRolled = row.IsRolled,
        IsHidden = row.IsHidden,
        IsTopmost = row.IsTopmost,
        IconSizePx = row.IconSizePx,
        Appearance = new WormholeAppearance { AccentOverride = row.AccentOverride, OpacityOverride = row.OpacityOverride },
        Portal = new PortalWormholeConfig
        {
            SourcePath = row.SourcePath,
            IncludeSubdirectoriesAsItems = row.IncludeSubdirectoriesAsItems,
            SortMode = row.SortMode,
        },
    };

    /// <summary>The fields of <paramref name="record"/> that differ from <paramref name="was"/>, or null when none does.</summary>
    private static WormholeWire.PatchArgs? BuildPatch(WormholeRecord record, WormholeWire.RowDto was)
    {
        static double? Moved(double now, double before) => Math.Abs(now - before) < 0.01 ? null : now;

        var g = record.Geometry;
        var patch = new WormholeWire.PatchArgs(
            record.Id,
            Title: record.Title != was.Title ? record.Title : null,
            IsLocked: record.IsLocked != was.IsLocked ? record.IsLocked : null,
            IsRolled: record.IsRolled != was.IsRolled ? record.IsRolled : null,
            IsHidden: record.IsHidden != was.IsHidden ? record.IsHidden : null,
            IsTopmost: record.IsTopmost != was.IsTopmost ? record.IsTopmost : null,
            IconSizePx: record.IconSizePx != was.IconSizePx ? record.IconSizePx : null,
            X: Moved(g.X, was.Geometry.X),
            Y: Moved(g.Y, was.Geometry.Y),
            Width: Moved(g.Width, was.Geometry.Width),
            Height: Moved(g.Height, was.Geometry.Height),
            UnrolledHeight: Moved(g.UnrolledHeight, was.Geometry.UnrolledHeight));

        return patch.Title is null && patch.IsLocked is null && patch.IsRolled is null && patch.IsHidden is null
               && patch.IsTopmost is null && patch.IconSizePx is null && patch.X is null && patch.Y is null
               && patch.Width is null && patch.Height is null && patch.UnrolledHeight is null
            ? null
            : patch;
    }

    private static WormholeGroup? ToGroup(WormholeWire.GroupDto? group)
        => group is null
            ? null
            // The wire carries no group id; the parent id is stable and unique per group.
            : new WormholeGroup { Id = group.ParentId, ParentId = group.ParentId, ActiveId = group.ActiveId, Members = group.Members.ToList() };

    // ------------------------------------------------------------------------------------------
    // IWormholeWindowManager
    // ------------------------------------------------------------------------------------------

    /// <summary>The satellite initialises itself when it starts; this completes when it says it is ready, or after 30
    /// seconds without it. Nothing is sent.</summary>
    public Task InitializeAsync(CancellationToken cancellationToken)
        => WaitReadyAsync(TimeSpan.FromSeconds(30), cancellationToken);

    /// <summary>Creates the wormhole in the satellite and returns a copy of it. When the satellite does not answer the
    /// result is an unsaved record with <see cref="Guid.Empty"/> as its id (logged, nothing thrown).</summary>
    public async Task<WormholeRecord> CreateAsync(string title, string sourceFolder, CancellationToken cancellationToken)
    {
        var fallback = new WormholeRecord
        {
            Id = Guid.Empty,
            Title = string.IsNullOrWhiteSpace(title) ? "Wormhole" : title,
            Portal = new PortalWormholeConfig { SourcePath = sourceFolder },
        };

        // The satellite may be starting (Explorer verb on a cold app): give it a moment to connect.
        if (!await WaitForChannelAsync(cancellationToken).ConfigureAwait(false)) return fallback;

        var answer = await SendAsync(WormholeWire.CreateRequest,
            WormholeWire.ToElement(new WormholeWire.CreateArgs(title, sourceFolder)), WormholeWire.LongTimeoutMs).ConfigureAwait(false);
        if (WormholeWire.ReadAnswer<WormholeWire.RowDto>(answer) is not { } row) return fallback;

        Upsert(row);
        return ToRecord(row);
    }

    public async Task DeleteAsync(Guid wormholeId, CancellationToken cancellationToken)
    {
        var answer = await SendAsync(WormholeWire.DeleteRequest, WormholeWire.ToElement(new WormholeWire.IdArgs(wormholeId))).ConfigureAwait(false);
        // The satellite also sends recordDeleted; dropping the row here keeps the cache right if that event is late.
        if (answer is not null) Remove(wormholeId);
    }

    public WormholeGroup? GroupFor(Guid wormholeId) => ToGroup(CachedRow(wormholeId)?.Group);

    public IReadOnlyList<WormholeRecord> TabsFor(Guid wormholeId)
    {
        if (CachedRow(wormholeId)?.Group is not { } group) return [];
        lock (_gate)
        {
            return group.Members
                .Select(id => _rows.FirstOrDefault(r => r.Id == id))
                .Where(r => r is not null)
                .Select(r => ToRecord(r!))
                .ToList();
        }
    }

    public Task MergeAsync(Guid dragged, Guid target, CancellationToken cancellationToken)
        => SendAsync(WormholeWire.MergeRequest, WormholeWire.ToElement(new WormholeWire.MergeArgs(dragged, target)), WormholeWire.LongTimeoutMs);

    public Task DetachAsync(Guid wormholeId, CancellationToken cancellationToken)
        => SendAsync(WormholeWire.DetachRequest, WormholeWire.ToElement(new WormholeWire.IdArgs(wormholeId)));

    public Task SetActiveTabAsync(Guid wormholeId, CancellationToken cancellationToken)
        => SendAsync(WormholeWire.SetActiveTabRequest, WormholeWire.ToElement(new WormholeWire.IdArgs(wormholeId)));

    public void ShowTabOnHover(Guid wormholeId)
        => SendCommand(WormholeWire.ShowTabOnHoverCommand, WormholeWire.ToElement(new WormholeWire.IdArgs(wormholeId)));

    public void HighlightMergeTarget(Guid? wormholeId)
        => SendCommand(WormholeWire.HighlightMergeTargetCommand, WormholeWire.ToElement(new WormholeWire.OptionalIdArgs(wormholeId)));

    public void ClearMergeHighlight() => SendCommand(WormholeWire.ClearMergeHighlightCommand);

    public void CloseAll() => SendCommand(WormholeWire.CloseAllCommand);

    public void RecenterAll() => SendCommand(WormholeWire.RecenterCommand);

    /// <summary>Sends the fields of <paramref name="record"/> that differ from the cached row as one patch. The satellite
    /// saves them and refreshes the live window; the answer (the updated row) refreshes the cache.</summary>
    public async Task ReconcileAsync(WormholeRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        var was = CachedRow(record.Id);
        if (was is null)
        {
            _logger.LogDebug("Wormholes: reconcile of an unknown wormhole {Id} skipped", record.Id);
            return;
        }

        var patch = BuildPatch(record, was);
        if (patch is null) return;

        var answer = await SendAsync(WormholeWire.PatchRequest, WormholeWire.ToElement(patch)).ConfigureAwait(false);
        if (WormholeWire.ReadAnswer<WormholeWire.RowDto>(answer) is { } row) Upsert(row);
    }

    public event EventHandler<Guid>? RecordChanged;
    public event EventHandler<Guid>? RecordDeleted;
    public event EventHandler<Guid>? WormholeFocused;
    public event EventHandler? WatchersPausedChanged;

    private Task BatchAsync(string op)
        => SendAsync(WormholeWire.BatchRequest, WormholeWire.ToElement(new WormholeWire.BatchArgs(op)), WormholeWire.LongTimeoutMs);

    public Task SetAllHiddenAsync(bool hidden, CancellationToken cancellationToken)
        => BatchAsync(hidden ? WormholeWire.BatchOps.HideAll : WormholeWire.BatchOps.ShowAll);

    public Task SetAllLockedAsync(bool locked, CancellationToken cancellationToken)
        => BatchAsync(locked ? WormholeWire.BatchOps.LockAll : WormholeWire.BatchOps.UnlockAll);

    public Task SetAllRolledAsync(bool rolled, CancellationToken cancellationToken)
        => BatchAsync(rolled ? WormholeWire.BatchOps.CollapseAll : WormholeWire.BatchOps.UncollapseAll);

    public Task ToggleAllHiddenAsync(CancellationToken cancellationToken) => BatchAsync(WormholeWire.BatchOps.ToggleHide);

    public Task ToggleAllLockedAsync(CancellationToken cancellationToken) => BatchAsync(WormholeWire.BatchOps.ToggleLock);

    public Task ToggleAllRolledAsync(CancellationToken cancellationToken) => BatchAsync(WormholeWire.BatchOps.ToggleCollapse);

    public Task SetAllTopmostAsync(bool topmost, CancellationToken cancellationToken)
        => BatchAsync(topmost ? WormholeWire.BatchOps.TopmostAll : WormholeWire.BatchOps.UntopmostAll);

    public Task ToggleAllTopmostAsync(CancellationToken cancellationToken) => BatchAsync(WormholeWire.BatchOps.ToggleTopmost);

    // ---- Layout presets ----

    /// <summary>Asks the satellite for the preset names and refreshes the cache. The cached names when it does not answer.</summary>
    public async Task<IReadOnlyList<string>> ListPresetsAsync(CancellationToken cancellationToken)
    {
        var presets = await RefreshPresetsAsync().ConfigureAwait(false);
        return presets?.Names ?? [];
    }

    /// <summary>The folder with one JSON per preset. The satellite's own answer once the presets are cached, the standard
    /// location until then.</summary>
    public string PresetsFolderPath
        => _presets?.FolderPath ?? Path.Combine(_paths.ResolveRoot(), WormholesFolderName, PresetsFolderName);

    public async Task<string?> CurrentSetupPresetAsync(CancellationToken cancellationToken)
    {
        var presets = _presets ?? await RefreshPresetsAsync().ConfigureAwait(false);
        return presets?.CurrentSetup;
    }

    private async Task PresetRequestAsync(string request, JsonElement data, int timeoutMs = WormholeWire.DefaultTimeoutMs)
    {
        var answer = await SendAsync(request, data, timeoutMs).ConfigureAwait(false);
        // The satellite also announces the change; refreshing here makes the cache current before the caller reads it.
        if (answer is not null) await RefreshPresetsAsync().ConfigureAwait(false);
    }

    public Task SaveCurrentAsPresetAsync(string name, CancellationToken cancellationToken)
        => PresetRequestAsync(WormholeWire.PresetsSaveRequest, WormholeWire.ToElement(new WormholeWire.NameArgs(name)));

    public Task DeletePresetAsync(string name, CancellationToken cancellationToken)
        => PresetRequestAsync(WormholeWire.PresetsDeleteRequest, WormholeWire.ToElement(new WormholeWire.NameArgs(name)));

    public Task RestorePresetAsync(string name, CancellationToken cancellationToken)
        => PresetRequestAsync(WormholeWire.PresetsRestoreRequest, WormholeWire.ToElement(new WormholeWire.NameArgs(name)), WormholeWire.LongTimeoutMs);

    /// <exception cref="ArgumentException">The satellite refused the new name; the message is its text.</exception>
    public async Task RenamePresetAsync(string oldName, string newName, CancellationToken cancellationToken)
    {
        var (answer, refusal) = await SendKeepingRefusalAsync(WormholeWire.PresetsRenameRequest,
            WormholeWire.ToElement(new WormholeWire.RenameArgs(oldName, newName))).ConfigureAwait(false);
        if (refusal is not null) throw new ArgumentException(refusal);
        if (answer is not null) await RefreshPresetsAsync().ConfigureAwait(false);
    }

    // ---- Source folder recovery ----

    public void RequestSourceRecovery(WormholeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        SendCommand(WormholeWire.RecoverSourceCommand, WormholeWire.ToElement(new WormholeWire.IdArgs(record.Id)));
    }

    public void RelinkSource(WormholeRecord record, string newSourcePath)
    {
        ArgumentNullException.ThrowIfNull(record);
        _ = SendAsync(WormholeWire.SourcesRelinkRequest,
            WormholeWire.ToElement(new WormholeWire.RelinkArgs(record.Id, newSourcePath)), WormholeWire.LongTimeoutMs);
    }

    public async Task<IReadOnlyList<WormholeRecord>> MissingSourcesAsync(CancellationToken cancellationToken)
    {
        var answer = await SendAsync(WormholeWire.SourcesMissingRequest).ConfigureAwait(false);
        return WormholeWire.ReadAnswer<WormholeWire.RowDto[]>(answer)?.Select(ToRecord).ToList() ?? [];
    }

    public async Task<int> RelinkMissingSourcesAsync(string newBaseFolder, CancellationToken cancellationToken)
    {
        var answer = await SendAsync(WormholeWire.SourcesRelinkRequest,
            WormholeWire.ToElement(new WormholeWire.RelinkArgs(null, newBaseFolder)), WormholeWire.LongTimeoutMs).ConfigureAwait(false);
        return WormholeWire.ReadAnswer<WormholeWire.RelinkResult>(answer)?.Count ?? 0;
    }

    // ---- Watchers ----

    public bool WatchersPaused => _watchersPaused;

    public void PauseWatchers()
    {
        SendCommand(WormholeWire.WatchersPauseCommand);
        SetWatchersPaused(true);
    }

    public void ResumeWatchers()
    {
        SendCommand(WormholeWire.WatchersResumeCommand);
        SetWatchersPaused(false);
    }

    /// <summary>The window handles of the satellite's wormholes, as of the last refresh. They only identify windows of
    /// another process, so nothing in the main app can use them as windows. Returns at once; a refresh is asked for
    /// every call.</summary>
    public IReadOnlyList<IntPtr> LiveWindowHandles()
    {
        _ = RefreshHandlesAsync();
        return _handles.Select(h => new IntPtr(h)).ToList();
    }

    private async Task RefreshHandlesAsync()
    {
        var answer = await SendAsync(WormholeWire.WindowHandlesRequest).ConfigureAwait(false);
        if (WormholeWire.ReadAnswer<WormholeWire.WindowHandlesDto>(answer) is { } handles) _handles = handles.Handles;
    }

    // ------------------------------------------------------------------------------------------
    // IWormholeRowSource
    // ------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<WormholeRecord>> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await RefreshRowsAsync().ConfigureAwait(false);
        return rows.Select(ToRecord).ToList();
    }

    public WormholeRecord? Snapshot(Guid wormholeId) => CachedRow(wormholeId) is { } row ? ToRecord(row) : null;

    public async Task<int> MoveAsync(Guid wormholeId, int delta, CancellationToken cancellationToken)
    {
        var answer = await SendAsync(WormholeWire.MoveRequest, WormholeWire.ToElement(new WormholeWire.MoveArgs(wormholeId, delta))).ConfigureAwait(false);
        if (WormholeWire.ReadAnswer<WormholeWire.MoveResult>(answer) is not { Index: >= 0 } moved) return -1;

        lock (_gate)
        {
            var from = _rows.FindIndex(r => r.Id == wormholeId);
            if (from >= 0 && moved.Index < _rows.Count)
            {
                var row = _rows[from];
                _rows.RemoveAt(from);
                _rows.Insert(moved.Index, row);
            }
        }
        return moved.Index;
    }

    public WormholeWire.DefaultsDto Defaults => _defaults;

    public async Task<WormholeWire.DefaultsDto> LoadDefaultsAsync(CancellationToken cancellationToken)
    {
        var answer = await SendAsync(WormholeWire.DefaultsGetRequest).ConfigureAwait(false);
        if (WormholeWire.ReadAnswer<WormholeWire.DefaultsDto>(answer) is { } defaults) _defaults = defaults;
        return _defaults;
    }

    public async Task SetDefaultAsync(string key, string value, CancellationToken cancellationToken)
    {
        var answer = await SendAsync(WormholeWire.DefaultsSetRequest, WormholeWire.ToElement(new WormholeWire.DefaultsSetArgs(key, value))).ConfigureAwait(false);
        if (WormholeWire.ReadAnswer<WormholeWire.DefaultsDto>(answer) is { } defaults) _defaults = defaults;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _satellite.Server.Connected -= OnConnected;
        SatelliteChannel? subscribed;
        lock (_gate)
        {
            subscribed = _subscribed;
            _subscribed = null;
        }
        if (subscribed is not null) subscribed.EventReceived -= OnEvent;
        // The satellite closes its own windows when the main app exits or its channel ends.
        Volatile.Read(ref _ready).TrySetResult();
    }
}
