using System.IO;
using AresToys.Storage.Paths;
using AresToys.Storage.Settings;
using Microsoft.Extensions.Logging;

namespace AresToys.App.Services.NewPlus;

/// <summary>
/// Keeps the Explorer "New+" submenu (<see cref="NewPlusContextMenuRegistration"/>) in step with the
/// template folder and the New+ settings. A singleton: the settings tab calls
/// <see cref="ApplyAsync"/> after every change, startup calls it once, and from then on a watcher on
/// the template folder, a language switch and the turn of the day (menu titles can carry a date)
/// reach it on their own. Changes are debounced so a burst of file events rewrites the registry once.
/// </summary>
public sealed class NewPlusContextMenuSync : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);

    private readonly ISettingsStore _store;
    private readonly IStoragePathResolver _paths;
    private readonly LocalizationService _localization;
    private readonly ILogger<NewPlusContextMenuSync> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _timerLock = new();
    private System.Threading.Timer? _debounceTimer;
    private System.Threading.Timer? _midnightTimer;
    private FileSystemWatcher? _watcher;
    private string? _watchedFolder;
    private string? _lastSignature;
    private bool _disposed;

    public NewPlusContextMenuSync(
        ISettingsStore store,
        IStoragePathResolver paths,
        LocalizationService localization,
        ILogger<NewPlusContextMenuSync> logger)
    {
        _store = store;
        _paths = paths;
        _localization = localization;
        _logger = logger;
        localization.CultureChanged += OnChanged;
    }

    /// <summary>Reads the settings and brings the registry in line: writes the menu and watches the
    /// template folder when New+ is on, removes both when it is off. The built-in "New" entry is
    /// hidden only while New+ is on. Never throws: failures are logged. Safe to call often.</summary>
    public async Task ApplyAsync()
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try { await ApplyCoreAsync().ConfigureAwait(false); }
            finally { _gate.Release(); }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not sync the Explorer New+ menu");
        }
    }

    /// <summary>Same as <see cref="ApplyAsync"/> but forces a rewrite even when nothing changed.</summary>
    public Task RewriteAsync()
    {
        _lastSignature = null;
        return ApplyAsync();
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_timerLock)
        {
            _debounceTimer?.Dispose(); _debounceTimer = null;
            _midnightTimer?.Dispose(); _midnightTimer = null;
        }
        _localization.CultureChanged -= OnChanged;
        StopWatching();
        _gate.Dispose();
    }

    // Caller holds the gate.
    private async Task ApplyCoreAsync()
    {
        var settings = await NewPlusSettings.LoadAsync(_store, _paths, CancellationToken.None).ConfigureAwait(false);

        if (!settings.Enabled)
        {
            StopWatching();
            StopMidnightTimer();
            _lastSignature = null;
            await Task.Run(() =>
            {
                NewPlusContextMenuRegistration.Unregister();
                SafeSetBuiltInHidden(false);
            }).ConfigureAwait(false);
            return;
        }

        // The folder must exist for the watcher and for "Open templates".
        try { Directory.CreateDirectory(settings.TemplateFolder); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not create the New+ template folder {Folder}", settings.TemplateFolder); }

        StartWatching(settings.TemplateFolder);
        ScheduleMidnight();

        var now = DateTime.Now;
        var entries = await Task.Run(() => BuildEntries(settings, now)).ConfigureAwait(false);
        var signature = string.Join('\n', entries.Select(e => e.TemplatePath + '\t' + e.Title + '\t' + e.Icon))
            + '\n' + settings.TemplateFolder
            + '\n' + (Markup.LocalizedStrings.Instance.Culture?.Name ?? string.Empty)
            + '\n' + settings.HideBuiltInNew;
        if (signature == _lastSignature && NewPlusContextMenuRegistration.IsRegistered()) return;

        // Registry and shell notification off the caller's thread (it may be the UI thread).
        await Task.Run(() =>
        {
            NewPlusContextMenuRegistration.Register(entries, settings.TemplateFolder);
            SafeSetBuiltInHidden(settings.HideBuiltInNew);
        }).ConfigureAwait(false);
        _lastSignature = signature;
    }

    private static List<NewPlusContextMenuRegistration.Entry> BuildEntries(NewPlusSettings settings, DateTime now)
        => NewPlusTemplates.List(settings.TemplateFolder)
            .Select(path => new NewPlusContextMenuRegistration.Entry(
                path, NewPlusTemplates.MenuTitle(path, settings, now), NewPlusNative.IconLocation(path)))
            .ToList();

    private void SafeSetBuiltInHidden(bool hidden)
    {
        try { NewPlusContextMenuRegistration.SetBuiltInNewHidden(hidden); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not update the built-in New context menu"); }
    }

    private void StartWatching(string folder)
    {
        if (_watcher is not null && string.Equals(_watchedFolder, folder, StringComparison.OrdinalIgnoreCase)) return;
        StopWatching();
        try
        {
            // Only the entries directly in the folder are templates, so no subfolder events.
            var watcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Attributes,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            watcher.Created += (_, _) => ScheduleApply();
            watcher.Deleted += (_, _) => ScheduleApply();
            watcher.Renamed += (_, _) => ScheduleApply();
            watcher.Changed += (_, _) => ScheduleApply();
            _watcher = watcher;
            _watchedFolder = folder;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not watch the New+ template folder {Folder}", folder);
        }
    }

    private void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
        _watchedFolder = null;
    }

    // Titles can carry a date, so the menu is rewritten when the day turns.
    private void ScheduleMidnight()
    {
        lock (_timerLock)
        {
            if (_disposed) return;
            var now = DateTime.Now;
            var wait = now.Date.AddDays(1) - now + TimeSpan.FromSeconds(5);
            _midnightTimer ??= new System.Threading.Timer(_ => { _ = RewriteAsync(); }, null,
                System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            _midnightTimer.Change(wait, System.Threading.Timeout.InfiniteTimeSpan);
        }
    }

    private void StopMidnightTimer()
    {
        lock (_timerLock) { _midnightTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite); }
    }

    private void OnChanged(object? sender, EventArgs e) => ScheduleApply();

    private void ScheduleApply()
    {
        lock (_timerLock)
        {
            if (_disposed) return;
            _debounceTimer ??= new System.Threading.Timer(_ => { _ = ApplyAsync(); }, null,
                System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            _debounceTimer.Change(Debounce, System.Threading.Timeout.InfiniteTimeSpan);
        }
    }
}
