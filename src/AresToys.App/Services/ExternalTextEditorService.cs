using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using AresToys.Core.Domain;
using AresToys.Storage.Items;
using AresToys.Storage.Settings;

namespace AresToys.App.Services;

/// <summary>Open a text-shaped clipboard row in an external editor (VSCode, Notepad++, …)
/// and sync edits back into the SQLite store. The flow:
/// <list type="number">
///   <item>Decode the row's payload as UTF-8 → write to a temp file under
///         <c>%LOCALAPPDATA%\AresToys\edit\</c> with a stable name keyed by the item id.</item>
///   <item>Launch the editor — explicit path from <c>editor.external_command</c> setting if
///         set, otherwise <see cref="ProcessStartInfo.UseShellExecute"/>=true so Windows opens
///         the user's default <c>.txt</c> handler.</item>
///   <item>Watch the file with <see cref="FileSystemWatcher"/> for changes; debounce ~250ms
///         (editors save in 2-3 IO bursts — tmp + rename + flush).</item>
///   <item>On change, read the file back, replace the row's payload via
///         <see cref="IItemStore.UpdatePayloadAsync"/>, and emit ItemsChanged so the popup
///         list refreshes automatically.</item>
/// </list>
/// One watcher per item-id; reopening the same item reuses the existing watcher rather than
/// stacking duplicates. Watchers self-dispose after 1 hour of inactivity (the user closed the
/// editor and moved on) so we don't leak file handles across days of uptime.</summary>
public sealed class ExternalTextEditorService : IDisposable
{
    public const string ExternalCommandKey = "editor.external_command";

    private readonly IItemStore _items;
    private readonly ISettingsStore _settings;
    private readonly ILogger<ExternalTextEditorService> _logger;
    private readonly string _editFolder;
    private readonly Dictionary<long, EditSession> _sessions = new();
    private readonly object _lock = new();

    public ExternalTextEditorService(IItemStore items, ISettingsStore settings, ILogger<ExternalTextEditorService> logger)
    {
        _items = items;
        _settings = settings;
        _logger = logger;
        _editFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AresToys",
            "edit");
        Directory.CreateDirectory(_editFolder);
    }

    /// <summary>Open the given text row in the user's editor. Returns true if the editor was
    /// launched (or the file was already open and got refreshed); false on a hard failure
    /// (item missing, write failed, etc.). The caller is on the UI thread; we hand off to a
    /// worker for the IO and process launch but the FileSystemWatcher runs ambiently.</summary>
    public async Task<bool> EditAsync(long itemId, CancellationToken cancellationToken)
    {
        var record = await _items.GetByIdAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (record is null) return false;
        if (!IsTextLike(record.Kind))
        {
            _logger.LogInformation("ExternalTextEditor: item {Id} kind {Kind} is not text — skipping", itemId, record.Kind);
            return false;
        }

        var path = Path.Combine(_editFolder, $"item-{itemId}.txt");
        var text = Encoding.UTF8.GetString(record.Payload.Span);

        try
        {
            // Write WITHOUT BOM — VSCode default + most editors expect plain UTF-8. Matches
            // the on-disk convention the rest of the app uses.
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ExternalTextEditor: failed to write temp file {Path}", path);
            return false;
        }

        EnsureWatcher(itemId, path);

        var command = await _settings.GetAsync(ExternalCommandKey, cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = $"\"{path}\"",
                    UseShellExecute = true,
                });
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ExternalTextEditor: failed to launch editor for {Path}", path);
            return false;
        }
    }

    private void EnsureWatcher(long itemId, string path)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(itemId, out var existing))
            {
                existing.RescheduleExpiry();
                return;
            }
            var session = new EditSession(itemId, path, this);
            _sessions[itemId] = session;
        }
    }

    private async void OnFileChanged(long itemId, string path)
    {
        try
        {
            // Editors write in bursts — wait briefly, then read once. The session's debounce
            // already coalesces; this Delay is the second-pass settle.
            await Task.Delay(80).ConfigureAwait(false);
            string text;
            try { text = await File.ReadAllTextAsync(path).ConfigureAwait(false); }
            catch (IOException) { return; } // editor still has it open — next event re-tries

            var bytes = Encoding.UTF8.GetBytes(text);
            await _items.UpdatePayloadAsync(itemId, bytes, bytes.LongLength, newKind: null, CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("ExternalTextEditor: synced {Bytes} bytes back to item {Id}", bytes.Length, itemId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ExternalTextEditor: sync-back failed for item {Id}", itemId);
        }
    }

    private void RemoveSession(long itemId)
    {
        lock (_lock)
        {
            if (_sessions.Remove(itemId, out var session)) session.Dispose();
        }
    }

    private static bool IsTextLike(ItemKind kind) =>
        kind is ItemKind.Text or ItemKind.Html or ItemKind.Rtf;

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var s in _sessions.Values) s.Dispose();
            _sessions.Clear();
        }
    }

    /// <summary>One open file = one watcher + debounce timer + auto-expiry timer. Owns the
    /// FileSystemWatcher and the two Timers; caller's only job is to instantiate and dispose.</summary>
    private sealed class EditSession : IDisposable
    {
        private readonly long _itemId;
        private readonly string _path;
        private readonly ExternalTextEditorService _owner;
        private readonly FileSystemWatcher _watcher;
        private readonly System.Threading.Timer _debounce;
        private readonly System.Threading.Timer _expiry;
        private const int DebounceMs = 250;
        private const int ExpiryMs = 60 * 60 * 1000; // 1 hour

        public EditSession(long itemId, string path, ExternalTextEditorService owner)
        {
            _itemId = itemId;
            _path = path;
            _owner = owner;
            var dir = Path.GetDirectoryName(path)!;
            var name = Path.GetFileName(path);
            // Init the timer first so the watcher's lambdas can capture a non-null reference.
            _debounce = new System.Threading.Timer(_ => owner.OnFileChanged(_itemId, _path),
                null, Timeout.Infinite, Timeout.Infinite);
            _expiry = new System.Threading.Timer(_ => owner.RemoveSession(_itemId),
                null, ExpiryMs, Timeout.Infinite);
            _watcher = new FileSystemWatcher(dir, name)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => _debounce.Change(DebounceMs, Timeout.Infinite);
            _watcher.Created += (_, _) => _debounce.Change(DebounceMs, Timeout.Infinite);
        }

        public void RescheduleExpiry() => _expiry.Change(ExpiryMs, Timeout.Infinite);

        public void Dispose()
        {
            try { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); } catch { }
            try { _debounce.Dispose(); } catch { }
            try { _expiry.Dispose(); } catch { }
            try { if (File.Exists(_path)) File.Delete(_path); } catch { }
        }
    }
}
