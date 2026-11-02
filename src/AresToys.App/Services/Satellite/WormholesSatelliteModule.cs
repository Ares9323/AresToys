using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AresToys.App.Services.Logging;
using AresToys.App.Services.Wormholes;

namespace AresToys.App.Services.Satellite;

/// <summary>The Wormholes inside the satellite: hosts the real <see cref="WormholeWindowManager"/> with its windows,
/// JSON store, folder watchers and the colour wheel, in the non elevated process so Explorer can drop files on them,
/// and serves the main facing surface of <see cref="IWormholeWindowManager"/> over the channel. The names and payloads
/// are in <see cref="WormholeWire"/>.
/// <para>Handlers run on the thread pool, so each one hops to the dispatcher and, except at start, waits for the
/// manager to finish initialising. <c>RecordChanged</c> fires on every step of a drag; it is coalesced to one
/// <see cref="WormholeWire.RecordChangedEvent"/> per record per <see cref="WormholeWire.RecordChangedCoalesceMs"/> ms
/// carrying the latest state.</para></summary>
public sealed class WormholesSatelliteModule : ISatelliteModule
{
    /// <summary>The longest a request waits for the manager to finish initialising before it fails.</summary>
    private static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(30);

    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // UI thread only.
    private readonly HashSet<Guid> _pendingChanged = [];
    private SatelliteChannel? _channel;
    private Dispatcher? _dispatcher;
    private IWormholeStore? _store;
    private WormholeDefaultsService? _defaults;
    private IWormholeWindowManager? _manager;
    private ILogger? _logger;
    private DispatcherTimer? _coalesceTimer;
    private bool _stopped;

    public string Name => SatelliteModuleNames.Wormholes;

    public void Register(SatelliteChannel channel, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(services);
        _channel = channel;
        _dispatcher = Application.Current.Dispatcher;
        _store = services.GetRequiredService<IWormholeStore>();
        _defaults = services.GetRequiredService<WormholeDefaultsService>();
        _manager = services.GetRequiredService<IWormholeWindowManager>();
        _logger = services.GetService<ILoggerFactory>()?.CreateLogger<WormholesSatelliteModule>();

        _manager.RecordChanged += OnRecordChanged;
        _manager.RecordDeleted += OnRecordDeleted;
        _manager.WormholeFocused += OnFocused;
        _manager.WatchersPausedChanged += OnWatchersPausedChanged;

        channel.Handle(WormholeWire.ListRequest, _ => Serve(ListAsync));
        channel.Handle(WormholeWire.CreateRequest, data => Serve(() => CreateAsync(data)));
        channel.Handle(WormholeWire.DeleteRequest, data => Serve(() => DeleteAsync(data)));
        channel.Handle(WormholeWire.MoveRequest, data => Serve(() => MoveAsync(data)));
        channel.Handle(WormholeWire.PatchRequest, data => Serve(() => PatchAsync(data)));
        channel.Handle(WormholeWire.BatchRequest, data => Serve(() => BatchAsync(data)));
        channel.Handle(WormholeWire.MergeRequest, data => Serve(() => MergeAsync(data)));
        channel.Handle(WormholeWire.DetachRequest, data => Serve(() => DetachAsync(data)));
        channel.Handle(WormholeWire.SetActiveTabRequest, data => Serve(() => SetActiveTabAsync(data)));
        channel.Handle(WormholeWire.PresetsListRequest, _ => Serve(PresetsListAsync));
        channel.Handle(WormholeWire.PresetsSaveRequest, data => Serve(() => PresetAsync(data, (m, n) => m.SaveCurrentAsPresetAsync(n, CancellationToken.None))));
        channel.Handle(WormholeWire.PresetsDeleteRequest, data => Serve(() => PresetAsync(data, (m, n) => m.DeletePresetAsync(n, CancellationToken.None))));
        channel.Handle(WormholeWire.PresetsRestoreRequest, data => Serve(() => PresetAsync(data, (m, n) => m.RestorePresetAsync(n, CancellationToken.None))));
        channel.Handle(WormholeWire.PresetsRenameRequest, data => Serve(() => PresetRenameAsync(data)));
        channel.Handle(WormholeWire.SourcesMissingRequest, _ => Serve(MissingSourcesAsync));
        channel.Handle(WormholeWire.SourcesRelinkRequest, data => Serve(() => RelinkAsync(data)));
        channel.Handle(WormholeWire.DefaultsGetRequest, _ => Serve(DefaultsGetAsync));
        channel.Handle(WormholeWire.DefaultsSetRequest, data => Serve(() => DefaultsSetAsync(data)));
        channel.Handle(WormholeWire.WindowHandlesRequest, _ => Serve(WindowHandlesAsync));
        channel.EventReceived += OnCommand;
    }

    /// <summary>Loads the defaults, then lets the manager create a window for every visible record, then tells the
    /// main app. Requests that arrive meanwhile wait for it.</summary>
    public void Start()
    {
        _ = StartAsync();
    }

    public void Stop()
    {
        _stopped = true;
        try
        {
            if (_channel is not null) _channel.EventReceived -= OnCommand;
            if (_manager is not null)
            {
                _manager.RecordChanged -= OnRecordChanged;
                _manager.RecordDeleted -= OnRecordDeleted;
                _manager.WormholeFocused -= OnFocused;
                _manager.WatchersPausedChanged -= OnWatchersPausedChanged;
            }
            _coalesceTimer?.Stop();
            _manager?.CloseAll();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Wormholes module: closing the windows failed");
        }
        _ready.TrySetResult();
    }

    private async Task StartAsync()
    {
        try
        {
            // The defaults first: the first frame of every window reads them.
            await _defaults!.LoadAsync(CancellationToken.None);
            StartupTrace.Mark("satellite: wormhole defaults loaded");
            await StartupTrace.MeasureAsync("satellite: wormhole manager InitializeAsync", () => _manager!.InitializeAsync(CancellationToken.None));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Wormholes module: initialising failed");
        }
        finally
        {
            _ready.TrySetResult();
        }

        if (_stopped) return;
        _channel?.Publish(WormholeWire.ReadyEvent, WormholeWire.ToElement(new WormholeWire.ReadyDto(_manager!.WatchersPaused)));
    }

    // ------------------------------------------------------------------------------------------
    // Requests
    // ------------------------------------------------------------------------------------------

    /// <summary>Runs one request body on the dispatcher once the manager is ready.</summary>
    private async Task<JsonElement?> Serve(Func<Task<JsonElement?>> body)
    {
        var dispatcher = _dispatcher ?? throw new InvalidOperationException("The wormholes module is not registered.");
        return await dispatcher.InvokeAsync<Task<JsonElement?>>(async () =>
        {
            await _ready.Task.WaitAsync(ReadyWait);
            if (_stopped) throw new InvalidOperationException("The wormholes module is stopping.");
            return await body();
        }).Task.Unwrap();
    }

    private static JsonElement? Answer<T>(T value) => WormholeWire.ToElement(value);

    private static T Args<T>(JsonElement? data) where T : class
        => WormholeWire.Read<T>(data) ?? throw new ArgumentException("The request has no valid payload.");

    private async Task<WormholeRecord> FindAsync(Guid id)
        => (await _store!.LoadAllAsync(CancellationToken.None)).FirstOrDefault(r => r.Id == id)
           ?? throw new InvalidOperationException("Unknown wormhole.");

    private WormholeWire.RowDto ToRow(WormholeRecord record) => WormholeWire.RowDto.From(record, _manager!.GroupFor(record.Id));

    private async Task<JsonElement?> ListAsync()
    {
        var records = await _store!.LoadAllAsync(CancellationToken.None);
        return Answer(records.Select(ToRow).ToArray());
    }

    private async Task<JsonElement?> CreateAsync(JsonElement? data)
    {
        var args = Args<WormholeWire.CreateArgs>(data);
        var record = await _manager!.CreateAsync(args.Title ?? string.Empty, args.SourcePath, CancellationToken.None);
        return Answer(ToRow(record));
    }

    private async Task<JsonElement?> DeleteAsync(JsonElement? data)
    {
        await _manager!.DeleteAsync(Args<WormholeWire.IdArgs>(data).Id, CancellationToken.None);
        return null;
    }

    private async Task<JsonElement?> MoveAsync(JsonElement? data)
    {
        var args = Args<WormholeWire.MoveArgs>(data);
        var index = await _store!.MoveAsync(args.Id, args.Delta, CancellationToken.None);
        return Answer(new WormholeWire.MoveResult(index));
    }

    private async Task<JsonElement?> PatchAsync(JsonElement? data)
    {
        var args = Args<WormholeWire.PatchArgs>(data);
        var record = await FindAsync(args.Id);
        args.ApplyTo(record);
        await _store!.SaveAsync(record, CancellationToken.None);
        await _manager!.ReconcileAsync(record, CancellationToken.None);
        return Answer(ToRow(record));
    }

    private async Task<JsonElement?> BatchAsync(JsonElement? data)
    {
        var manager = _manager!;
        var ct = CancellationToken.None;
        var op = Args<WormholeWire.BatchArgs>(data).Op.ToLowerInvariant();
        switch (op)
        {
            case WormholeWire.BatchOps.HideAll: await manager.SetAllHiddenAsync(true, ct); break;
            case WormholeWire.BatchOps.ShowAll: await manager.SetAllHiddenAsync(false, ct); break;
            case WormholeWire.BatchOps.LockAll: await manager.SetAllLockedAsync(true, ct); break;
            case WormholeWire.BatchOps.UnlockAll: await manager.SetAllLockedAsync(false, ct); break;
            case WormholeWire.BatchOps.CollapseAll: await manager.SetAllRolledAsync(true, ct); break;
            case WormholeWire.BatchOps.UncollapseAll: await manager.SetAllRolledAsync(false, ct); break;
            case WormholeWire.BatchOps.TopmostAll: await manager.SetAllTopmostAsync(true, ct); break;
            case WormholeWire.BatchOps.UntopmostAll: await manager.SetAllTopmostAsync(false, ct); break;
            case WormholeWire.BatchOps.ToggleHide: await manager.ToggleAllHiddenAsync(ct); break;
            case WormholeWire.BatchOps.ToggleLock: await manager.ToggleAllLockedAsync(ct); break;
            case WormholeWire.BatchOps.ToggleCollapse: await manager.ToggleAllRolledAsync(ct); break;
            case WormholeWire.BatchOps.ToggleTopmost: await manager.ToggleAllTopmostAsync(ct); break;
            default: throw new ArgumentException($"Unknown batch op '{op}'.");
        }
        return null;
    }

    private async Task<JsonElement?> MergeAsync(JsonElement? data)
    {
        var args = Args<WormholeWire.MergeArgs>(data);
        await _manager!.MergeAsync(args.Dragged, args.Target, CancellationToken.None);
        MarkChanged(args.Dragged);
        MarkChanged(args.Target);
        return null;
    }

    private async Task<JsonElement?> DetachAsync(JsonElement? data)
    {
        var id = Args<WormholeWire.IdArgs>(data).Id;
        await _manager!.DetachAsync(id, CancellationToken.None);
        MarkChanged(id);
        return null;
    }

    private async Task<JsonElement?> SetActiveTabAsync(JsonElement? data)
    {
        var id = Args<WormholeWire.IdArgs>(data).Id;
        await _manager!.SetActiveTabAsync(id, CancellationToken.None);
        MarkChanged(id);
        return null;
    }

    private async Task<JsonElement?> PresetsListAsync()
    {
        var manager = _manager!;
        var names = await manager.ListPresetsAsync(CancellationToken.None);
        var current = await manager.CurrentSetupPresetAsync(CancellationToken.None);
        return Answer(new WormholeWire.PresetListDto(names.ToArray(), current, manager.PresetsFolderPath));
    }

    private async Task<JsonElement?> PresetAsync(JsonElement? data, Func<IWormholeWindowManager, string, Task> action)
    {
        await action(_manager!, Args<WormholeWire.NameArgs>(data).Name);
        PublishPresetsChanged();
        return null;
    }

    private async Task<JsonElement?> PresetRenameAsync(JsonElement? data)
    {
        var args = Args<WormholeWire.RenameArgs>(data);
        await _manager!.RenamePresetAsync(args.OldName, args.NewName, CancellationToken.None);
        PublishPresetsChanged();
        return null;
    }

    private async Task<JsonElement?> MissingSourcesAsync()
    {
        var missing = await _manager!.MissingSourcesAsync(CancellationToken.None);
        return Answer(missing.Select(ToRow).ToArray());
    }

    private async Task<JsonElement?> RelinkAsync(JsonElement? data)
    {
        var args = Args<WormholeWire.RelinkArgs>(data);
        if (args.Id is not { } id)
            return Answer(new WormholeWire.RelinkResult(await _manager!.RelinkMissingSourcesAsync(args.Path, CancellationToken.None)));

        var record = await FindAsync(id);
        var exists = !string.IsNullOrWhiteSpace(args.Path) && Directory.Exists(args.Path);
        _manager!.RelinkSource(record, args.Path);
        return Answer(new WormholeWire.RelinkResult(exists ? 1 : 0));
    }

    private Task<JsonElement?> DefaultsGetAsync()
        => Task.FromResult(Answer(WormholeWire.DefaultsDto.From(_defaults!)));

    private async Task<JsonElement?> DefaultsSetAsync(JsonElement? data)
    {
        var args = Args<WormholeWire.DefaultsSetArgs>(data);
        await WormholeWire.SetDefaultAsync(_defaults!, args.Key, args.Value, CancellationToken.None);
        var now = WormholeWire.DefaultsDto.From(_defaults!);
        _channel?.Publish(WormholeWire.DefaultsChangedEvent, WormholeWire.ToElement(now));
        return Answer(now);
    }

    private Task<JsonElement?> WindowHandlesAsync()
        => Task.FromResult(Answer(new WormholeWire.WindowHandlesDto(_manager!.LiveWindowHandles().Select(h => (long)h).ToArray())));

    private void PublishPresetsChanged() => _channel?.Publish(WormholeWire.PresetsChangedEvent);

    // ------------------------------------------------------------------------------------------
    // Commands from the main app (fire and forget; they arrive in order, off the UI thread)
    // ------------------------------------------------------------------------------------------

    private void OnCommand(string name, JsonElement? data)
    {
        if (!name.StartsWith("wormholes.", StringComparison.Ordinal)) return;
        _ = DispatchCommandAsync(name, data);
    }

    private async Task DispatchCommandAsync(string name, JsonElement? data)
    {
        try
        {
            var dispatcher = _dispatcher;
            if (dispatcher is null) return;
            await dispatcher.InvokeAsync<Task>(async () =>
            {
                await _ready.Task.WaitAsync(ReadyWait);
                if (_stopped) return;
                await RunCommandAsync(name, data);
            }).Task.Unwrap();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Wormholes module: command '{Name}' failed", name);
        }
    }

    /// <summary>UI thread only.</summary>
    private async Task RunCommandAsync(string name, JsonElement? data)
    {
        var manager = _manager!;
        switch (name)
        {
            case WormholeWire.RecenterCommand: manager.RecenterAll(); break;
            case WormholeWire.WatchersPauseCommand: manager.PauseWatchers(); break;
            case WormholeWire.WatchersResumeCommand: manager.ResumeWatchers(); break;
            case WormholeWire.CloseAllCommand: manager.CloseAll(); break;
            case WormholeWire.ClearMergeHighlightCommand: manager.ClearMergeHighlight(); break;
            case WormholeWire.HighlightMergeTargetCommand:
                manager.HighlightMergeTarget(WormholeWire.Read<WormholeWire.OptionalIdArgs>(data)?.Id);
                break;
            case WormholeWire.ShowTabOnHoverCommand:
                if (WormholeWire.Read<WormholeWire.IdArgs>(data) is { } hover) manager.ShowTabOnHover(hover.Id);
                break;
            case WormholeWire.RecoverSourceCommand:
                if (WormholeWire.Read<WormholeWire.IdArgs>(data) is { } recover) manager.RequestSourceRecovery(await FindAsync(recover.Id));
                break;
        }
    }

    // ------------------------------------------------------------------------------------------
    // Events to the main app (manager events fire on the UI thread)
    // ------------------------------------------------------------------------------------------

    private void OnRecordChanged(object? sender, Guid id) => MarkChanged(id);

    /// <summary>Remembers that a record changed and arms the trailing timer. The row is built when the timer fires, so
    /// it carries the latest state, and a record that changes on every step of a drag costs one event per interval.</summary>
    private void MarkChanged(Guid id)
    {
        if (_stopped) return;
        _pendingChanged.Add(id);
        if (_coalesceTimer is null)
        {
            _coalesceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(WormholeWire.RecordChangedCoalesceMs) };
            _coalesceTimer.Tick += (_, _) => FlushChanged();
        }
        if (!_coalesceTimer.IsEnabled) _coalesceTimer.Start();
    }

    private void FlushChanged()
    {
        _coalesceTimer?.Stop();
        if (_stopped || _pendingChanged.Count == 0) return;

        var ids = _pendingChanged.ToList();
        _pendingChanged.Clear();
        _ = PublishChangedAsync(ids);
    }

    private async Task PublishChangedAsync(List<Guid> ids)
    {
        try
        {
            var records = (await _store!.LoadAllAsync(CancellationToken.None)).ToDictionary(r => r.Id);
            foreach (var id in ids)
            {
                // A record deleted since is announced by the deleted event only.
                if (records.TryGetValue(id, out var record))
                    _channel?.Publish(WormholeWire.RecordChangedEvent, WormholeWire.ToElement(ToRow(record)));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Wormholes module: publishing the changed records failed");
        }
    }

    private void OnRecordDeleted(object? sender, Guid id)
    {
        _pendingChanged.Remove(id);
        _channel?.Publish(WormholeWire.RecordDeletedEvent, WormholeWire.ToElement(new WormholeWire.IdArgs(id)));
    }

    private void OnFocused(object? sender, Guid id)
        => _channel?.Publish(WormholeWire.FocusedEvent, WormholeWire.ToElement(new WormholeWire.IdArgs(id)));

    private void OnWatchersPausedChanged(object? sender, EventArgs e)
        => _channel?.Publish(WormholeWire.WatchersPausedEvent, WormholeWire.ToElement(new WormholeWire.PausedDto(_manager!.WatchersPaused)));
}

/// <summary>The satellite shows no toasts: the colour wheel's eyedropper never raises one, and a second process must
/// not hook the toast activation that the main app owns.</summary>
internal sealed class SatelliteToastNotifier : IToastNotifier
{
    public void Show(string title, string message, Action? onClick = null, string? imagePath = null,
                     IReadOnlyList<ToastButtonChoice>? buttons = null)
    {
    }
}
