using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using AresToys.App.Views;
using AresToys.Storage.Settings;

namespace AresToys.App.Services.Pins;

/// <summary>Keeps <see cref="PinStore"/> in sync with the live pinned windows so they can be
/// restored at the next launch ("Restore pinned images at startup", default ON).
///
/// <list type="bullet">
/// <item>A new image pin writes its PNG once, off the UI thread, streamed from the bitmap the
/// window already holds: no extra in-memory copy.</item>
/// <item>Moves (end of drag), zoom, opacity, border and mute changes are debounced, then written.</item>
/// <item>Only a close by the user (Esc / right click) removes the entry. Windows closed by app
/// shutdown keep theirs, and a Velopack update restart exits without closing them at all.</item>
/// </list>
///
/// UI-thread API; disk work runs on one serial background queue so writes keep their order.</summary>
public sealed class PinPersistenceService
{
    public const string RestoreSettingKey = "pin.restore_at_startup";

    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);

    private readonly PinStore _store;
    private readonly ISettingsStore _settings;
    private readonly ILogger<PinPersistenceService> _logger;
    private readonly Dictionary<Guid, Tracked> _tracked = new();
    private readonly object _queueGate = new();
    private Task _queue = Task.CompletedTask;
    private bool _enabled = true;

    public PinPersistenceService(PinStore store, ISettingsStore settings, ILogger<PinPersistenceService> logger)
    {
        _store = store;
        _settings = settings;
        _logger = logger;
    }

    public bool IsEnabled => _enabled;

    /// <summary>Read the setting (unset = ON). When OFF, anything left on disk is cleared so a
    /// later re-enable never resurrects old pins.</summary>
    public async Task<bool> LoadEnabledAsync(CancellationToken cancellationToken)
    {
        var raw = await _settings.GetAsync(RestoreSettingKey, cancellationToken).ConfigureAwait(true);
        _enabled = !string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase);
        if (!_enabled) Enqueue("clear", _store.Clear);
        return _enabled;
    }

    /// <summary>Settings toggle. OFF stops persisting and deletes the saved pins; ON starts
    /// persisting the pins open right now. UI-thread only.</summary>
    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        await _settings.SetAsync(RestoreSettingKey, enabled ? "true" : "false", sensitive: false, cancellationToken).ConfigureAwait(true);
        if (_enabled == enabled) return;
        _enabled = enabled;
        if (!enabled)
        {
            foreach (var t in _tracked.Values)
            {
                t.Debounce.Stop();
                t.Persisted = false;
            }
            Enqueue("clear", _store.Clear);
            return;
        }
        foreach (var t in _tracked.Values) PersistNow(t);
    }

    /// <summary>Manifest entries to restore, pruned of missing files. Disk work: call off the UI thread.</summary>
    public IReadOnlyList<PinRecord> LoadForRestore() => _store.LoadAndPrune();

    public string ImagePath(PinRecord record) => Path.Combine(_store.FolderPath, record.FileName ?? string.Empty);

    /// <summary>Drop an entry that could not be restored (undecodable PNG and similar).</summary>
    public void Forget(Guid id) => Enqueue("remove", () => _store.Remove(id));

    /// <summary>Start following an image pin. Call after the window is shown.
    /// <paramref name="alreadyPersisted"/> is true for a restored pin whose PNG is on disk.</summary>
    public void TrackImage(PinnedImageWindow window, bool alreadyPersisted = false)
    {
        ArgumentNullException.ThrowIfNull(window);
        var t = Track(window, window.PinId, window.CaptureRecord, () => window.ClosedByUser, image: window, alreadyPersisted);
        if (t is null) return;
        window.PinStateChanged += (_, _) => ScheduleSave(t);
        window.BitmapReplaced += (_, _) =>
        {
            if (!_enabled) return;
            t.Persisted = false;
            PersistNow(t);
        };
    }

    /// <summary>Start following a video pin. Call after the window is shown.</summary>
    public void TrackVideo(PinnedVideoWindow window, bool alreadyPersisted = false)
    {
        ArgumentNullException.ThrowIfNull(window);
        var t = Track(window, window.PinId, window.CaptureRecord, () => window.ClosedByUser, image: null, alreadyPersisted);
        if (t is null) return;
        window.PinStateChanged += (_, _) => ScheduleSave(t);
    }

    /// <summary>Wait for the queued disk writes (tests, diagnostics).</summary>
    public Task FlushAsync()
    {
        lock (_queueGate) return _queue;
    }

    private Tracked? Track(Window window, Guid id, Func<PinRecord?> capture, Func<bool> closedByUser,
        PinnedImageWindow? image, bool alreadyPersisted)
    {
        if (_tracked.ContainsKey(id)) return null;
        var t = new Tracked(id, capture, image)
        {
            Persisted = alreadyPersisted,
            Debounce = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = SaveDelay },
        };
        t.Debounce.Tick += (_, _) =>
        {
            t.Debounce.Stop();
            SaveState(t);
        };
        _tracked[id] = t;

        // Closing, not Closed: the HWND still exists, so a pending change can still be captured.
        window.Closing += (_, e) =>
        {
            if (e.Cancel || closedByUser() || !t.Debounce.IsEnabled) return;
            t.Debounce.Stop();
            // Shutdown path: write synchronously, the process may be gone before a queued task runs.
            if (!_enabled || !t.Persisted || capture() is not { } record) return;
            try
            {
                FlushAsync().Wait(TimeSpan.FromSeconds(2));
                record.FileName = t.FileName;
                _store.Upsert(record);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Pins: final save of {Id} failed", id); }
        };
        window.Closed += (_, _) =>
        {
            t.Debounce.Stop();
            _tracked.Remove(id);
            if (!closedByUser()) return;
            if (_enabled) Enqueue("remove", () => _store.Remove(id));
        };

        if (_enabled && !alreadyPersisted) PersistNow(t);
        return t;
    }

    private void ScheduleSave(Tracked t)
    {
        if (!_enabled) return;
        t.Debounce.Stop();
        t.Debounce.Start();
    }

    private void SaveState(Tracked t)
    {
        if (!_enabled) return;
        if (!t.Persisted)
        {
            PersistNow(t);
            return;
        }
        if (t.Capture() is not { } record) return;
        record.FileName = t.FileName;
        Enqueue("update", () => _store.Upsert(record));
    }

    /// <summary>First write of a pin: PNG (image pins) then the manifest entry.</summary>
    private void PersistNow(Tracked t)
    {
        if (t.Capture() is not { } record) return;
        record.CreatedAt = DateTimeOffset.UtcNow;
        t.Persisted = true;
        if (t.Image is null)
        {
            Enqueue("add", () => _store.Upsert(record));
            return;
        }

        var bitmap = t.Image.Bitmap;
        var fileName = PinStore.ImageFileNameFor(t.Id);
        t.FileName = fileName;
        record.FileName = fileName;
        if (!bitmap.IsFrozen)
        {
            // Not shareable with the background thread: encode here, still straight to disk.
            try { _store.WriteImage(t.Id, s => EncodePng(bitmap, s)); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pins: could not save image of pin {Id}", t.Id);
                t.Persisted = false;
                return;
            }
            Enqueue("add", () => _store.Upsert(record));
            return;
        }
        Enqueue("add", () =>
        {
            _store.WriteImage(t.Id, s => EncodePng(bitmap, s));
            _store.Upsert(record);
        });
    }

    private static void EncodePng(BitmapSource bitmap, Stream destination)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(destination);
    }

    private void Enqueue(string what, Action work)
    {
        lock (_queueGate)
        {
            _queue = _queue.ContinueWith(_ =>
            {
                try { work(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Pins: {Operation} failed", what); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    private sealed class Tracked(Guid id, Func<PinRecord?> capture, PinnedImageWindow? image)
    {
        public Guid Id { get; } = id;
        public Func<PinRecord?> Capture { get; } = capture;
        public PinnedImageWindow? Image { get; } = image;
        public required DispatcherTimer Debounce { get; init; }
        public bool Persisted { get; set; }
        public string? FileName { get; set; } = image is null ? null : PinStore.ImageFileNameFor(id);
    }
}
