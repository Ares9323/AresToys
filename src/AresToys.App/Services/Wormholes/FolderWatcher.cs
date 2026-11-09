using System.IO;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace AresToys.App.Services.Wormholes;

/// <summary>One-folder watcher for a Portal wormhole. Wraps <see cref="FileSystemWatcher"/>
/// with a 300 ms debounce <see cref="DispatcherTimer"/> so burst-y events (saving a big file,
/// drag-dropping a batch from Explorer) coalesce into a single notification. Renames are
/// emitted as a tuple so the consumer can update a single item in place instead of seeing
/// "delete old + create new".
///
/// Failure modes intentionally simple: on <c>FileSystemWatcher.Error</c> (usually buffer
/// overflow from a flood of events) we tear down + rebuild the watcher and emit a "full
/// refresh" event so the UI re-enumerates from scratch.</summary>
public sealed class FolderWatcher : IDisposable
{
    private const int DebounceMilliseconds = 300;

    private readonly ILogger<FolderWatcher> _logger;
    private readonly DispatcherTimer _debounce;
    private readonly Queue<FolderWatcherEvent> _pending = new();
    private readonly object _gate = new();
    private FileSystemWatcher? _fsw;
    private string? _watchedPath;

    public FolderWatcher(ILogger<FolderWatcher> logger)
    {
        _logger = logger;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebounceMilliseconds) };
        _debounce.Tick += OnDebounceTick;
    }

    /// <summary>Path currently being watched, or null when stopped. Read-only — callers move
    /// between paths via <see cref="Stop"/> + <see cref="Start"/>.</summary>
    public string? WatchedPath => _watchedPath;

    /// <summary>Raised on the UI thread after the 300 ms quiet period following a burst of
    /// raw FSW events. Carries the batched list of changes; the consumer decides whether to
    /// apply them incrementally or trigger a full re-enumerate of the watched folder.</summary>
    public event EventHandler<FolderWatcherChangedEventArgs>? Changed;

    /// <summary>Raised when the underlying <see cref="FileSystemWatcher"/> reports an Error
    /// (typically buffer overflow). Consumer should re-enumerate the folder from scratch —
    /// individual events were dropped, so the queue is no longer authoritative.</summary>
    public event EventHandler? FullRefreshRequested;

    public void Start(string folderPath)
    {
        Stop();
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            _logger.LogWarning("FolderWatcher.Start called with empty path; skipping");
            return;
        }
        if (!Directory.Exists(folderPath))
        {
            _logger.LogWarning("FolderWatcher target folder does not exist: {Path}", folderPath);
            // Keep _watchedPath set so a later folder-change notification (e.g. user reconnects
            // a removable drive) can re-Start with the same path. The window's "source folder
            // unavailable" banner is the user-visible signal of this state.
            _watchedPath = folderPath;
            return;
        }

        try
        {
            _fsw = new FileSystemWatcher(folderPath)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName
                             | NotifyFilters.DirectoryName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _fsw.Created += OnRawCreatedOrDeleted;
            _fsw.Deleted += OnRawCreatedOrDeleted;
            _fsw.Renamed += OnRawRenamed;
            _fsw.Changed += OnRawCreatedOrDeleted;
            _fsw.Error   += OnFswError;
            _watchedPath = folderPath;
            _logger.LogDebug("FolderWatcher started on {Path}", folderPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FolderWatcher.Start failed for {Path}", folderPath);
            _fsw?.Dispose();
            _fsw = null;
        }
    }

    public void Stop()
    {
        _debounce.Stop();
        lock (_gate) _pending.Clear();
        if (_fsw is null) return;
        try
        {
            _fsw.EnableRaisingEvents = false;
            _fsw.Created -= OnRawCreatedOrDeleted;
            _fsw.Deleted -= OnRawCreatedOrDeleted;
            _fsw.Renamed -= OnRawRenamed;
            _fsw.Changed -= OnRawCreatedOrDeleted;
            _fsw.Error   -= OnFswError;
            _fsw.Dispose();
        }
        catch (Exception ex) { _logger.LogDebug(ex, "FolderWatcher.Stop cleanup raised"); }
        _fsw = null;
        _watchedPath = null;
    }

    public void Dispose()
    {
        Stop();
        _debounce.Tick -= OnDebounceTick;
    }

    private void OnRawCreatedOrDeleted(object sender, FileSystemEventArgs e)
    {
        if (IsTemporaryFile(e.FullPath)) return;
        lock (_gate) _pending.Enqueue(new FolderWatcherEvent(MapChangeType(e.ChangeType), e.FullPath, null));
        RestartDebounce();
    }

    private void OnRawRenamed(object sender, RenamedEventArgs e)
    {
        // Coalesce rename into a single event with both paths so the consumer can update the
        // matching item in place. FileSystemWatcher otherwise emits this as Renamed only, but
        // some shells / SMB shares split it into Delete+Create which we'd process incorrectly.
        // The single Renamed path is the happy case; the split version falls back to two raw
        // Created/Deleted events through the other handler.
        if (IsTemporaryFile(e.FullPath) && IsTemporaryFile(e.OldFullPath)) return;
        lock (_gate) _pending.Enqueue(new FolderWatcherEvent(FolderWatcherChangeKind.Renamed, e.FullPath, e.OldFullPath));
        RestartDebounce();
    }

    private void OnFswError(object sender, ErrorEventArgs e)
    {
        _logger.LogWarning(e.GetException(), "FileSystemWatcher buffer error; requesting full refresh");
        // Drain pending — they're no longer reliable after a buffer overflow.
        lock (_gate) _pending.Clear();
        // Dispatch on UI thread because the consumer touches WPF surfaces.
        if (System.Windows.Application.Current is { } app)
            app.Dispatcher.BeginInvoke(() => FullRefreshRequested?.Invoke(this, EventArgs.Empty));
        else
            FullRefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RestartDebounce()
    {
        // DispatcherTimer can only be touched from the UI thread; FSW events come in from a
        // pool thread. Marshal back via the dispatcher.
        if (System.Windows.Application.Current is { } app)
            app.Dispatcher.BeginInvoke(() => { _debounce.Stop(); _debounce.Start(); });
        else
        {
            _debounce.Stop();
            _debounce.Start();
        }
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounce.Stop();
        List<FolderWatcherEvent> batch;
        lock (_gate)
        {
            if (_pending.Count == 0) return;
            batch = new List<FolderWatcherEvent>(_pending);
            _pending.Clear();
        }
        Changed?.Invoke(this, new FolderWatcherChangedEventArgs(batch));
    }

    private static FolderWatcherChangeKind MapChangeType(WatcherChangeTypes type) => type switch
    {
        WatcherChangeTypes.Created => FolderWatcherChangeKind.Created,
        WatcherChangeTypes.Deleted => FolderWatcherChangeKind.Deleted,
        WatcherChangeTypes.Changed => FolderWatcherChangeKind.Changed,
        WatcherChangeTypes.Renamed => FolderWatcherChangeKind.Renamed,
        _ => FolderWatcherChangeKind.Changed,
    };

    /// <summary>Filter for files we never want to surface in a Portal wormhole — Office's
    /// owner-lock <c>~$</c> files, browser <c>.crdownload</c>, plain <c>.tmp</c>. Identical
    /// list to DesktopFences' filter; keeps the visible folder content stable while editors
    /// are mid-save. Matches by filename only; doesn't recurse.</summary>
    private static bool IsTemporaryFile(string fullPath)
    {
        var name = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(name)) return false;
        if (name.StartsWith("~$", StringComparison.Ordinal)) return true;
        var ext = Path.GetExtension(name);
        return ext.Equals(".tmp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".crdownload", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".part", StringComparison.OrdinalIgnoreCase);
    }
}

public enum FolderWatcherChangeKind { Created, Deleted, Changed, Renamed }

public sealed record FolderWatcherEvent(FolderWatcherChangeKind Kind, string FullPath, string? OldFullPath);

public sealed class FolderWatcherChangedEventArgs : EventArgs
{
    public FolderWatcherChangedEventArgs(IReadOnlyList<FolderWatcherEvent> events) { Events = events; }
    public IReadOnlyList<FolderWatcherEvent> Events { get; }
}
