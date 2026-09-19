using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using AresToys.App.Services.Wormholes;
using AresToys.Storage.Settings;

namespace AresToys.App.Tests.Wormholes;

/// <summary>In-memory settings store — enough for <see cref="WormholeDefaultsService"/> to
/// hydrate its fallbacks without touching disk.</summary>
internal sealed class FakeSettingsStore : ISettingsStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(_values.TryGetValue(key, out var v) ? v : null);

    public Task SetAsync(string key, string value, bool sensitive, CancellationToken cancellationToken)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(_values.Remove(key));

    public async IAsyncEnumerable<SettingEntry> EnumerateAsync(
        bool includeSensitive = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var (k, v) in _values)
        {
            yield return new SettingEntry(k, v, false);
        }
        await Task.CompletedTask;
    }
}

/// <summary>In-memory wormhole store holding whatever records a test seeds it with. Only the
/// members the Settings view-model actually exercises are implemented; the rest throw, so a test
/// that starts depending on them fails loudly instead of silently passing on a fake behaviour.</summary>
internal sealed class FakeWormholeStore : IWormholeStore
{
    public List<WormholeRecord> Records { get; } = new();

    public Task<IReadOnlyList<WormholeRecord>> LoadAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WormholeRecord>>(Records.ToList());

    public Task SaveAsync(WormholeRecord record, CancellationToken cancellationToken)
    {
        var existing = Records.FindIndex(r => r.Id == record.Id);
        if (existing >= 0) Records[existing] = record; else Records.Add(record);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid wormholeId, CancellationToken cancellationToken)
    {
        Records.RemoveAll(r => r.Id == wormholeId);
        return Task.CompletedTask;
    }

    public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public string WormholesRootPath => Path.Combine(Path.GetTempPath(), "arestoys-fake-wormholes");
    public string PresetsFolderPath => Path.Combine(WormholesRootPath, "presets");
    public string GetShortcutsDirectory(Guid wormholeId) => Path.Combine(WormholesRootPath, wormholeId.ToString("N"));

    public Task<IReadOnlyList<string>> ListPresetNamesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<string?> GetPresetNameForCurrentSetupAsync(CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public Task<int> MoveAsync(Guid wormholeId, int delta, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task SavePresetAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task DeletePresetAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task RenamePresetAsync(string oldName, string newName, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IReadOnlyDictionary<Guid, WormholeGeometry>?> GetPresetPositionsAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IReadOnlyDictionary<Guid, WormholePresetState>> GetPresetStatesAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task AssociateCurrentSetupAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task ApplyPositionsAsync(IReadOnlyDictionary<Guid, WormholeGeometry> positions, CancellationToken cancellationToken) => throw new NotSupportedException();
}

/// <summary>Window manager stand-in. Tests drive it through <see cref="RaiseRecordDeleted"/> /
/// <see cref="RaiseRecordChanged"/> to stand in for what the live wormhole chrome does.</summary>
internal sealed class FakeWormholeWindowManager : IWormholeWindowManager
{
    public List<Guid> Deleted { get; } = new();

    public void RaiseRecordDeleted(Guid id) => RecordDeleted?.Invoke(this, id);
    public void RaiseRecordChanged(Guid id) => RecordChanged?.Invoke(this, id);
    public void RaiseWormholeFocused(Guid id) => WormholeFocused?.Invoke(this, id);
    public void RaiseWatchersPausedChanged() => WatchersPausedChanged?.Invoke(this, EventArgs.Empty);

    public event EventHandler<Guid>? RecordChanged;
    public event EventHandler<Guid>? RecordDeleted;
    public event EventHandler<Guid>? WormholeFocused;
    public event EventHandler? WatchersPausedChanged;

    /// <summary>Mirrors the real manager: removes the record, then announces it.</summary>
    public Task DeleteAsync(Guid wormholeId, CancellationToken cancellationToken)
    {
        Deleted.Add(wormholeId);
        RecordDeleted?.Invoke(this, wormholeId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListPresetsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<string?> CurrentSetupPresetAsync(CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public string PresetsFolderPath => Path.Combine(Path.GetTempPath(), "arestoys-fake-presets");
    public bool WatchersPaused => false;

    public Task InitializeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<WormholeRecord> CreateAsync(string title, string sourceFolder, CancellationToken cancellationToken) => throw new NotSupportedException();
    public void CloseAll() => throw new NotSupportedException();
    public void RecenterAll() => throw new NotSupportedException();
    public Task ReconcileAsync(WormholeRecord record, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task SetAllHiddenAsync(bool hidden, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task SetAllLockedAsync(bool locked, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task SetAllRolledAsync(bool rolled, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task ToggleAllHiddenAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task ToggleAllLockedAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task ToggleAllRolledAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task SetAllTopmostAsync(bool topmost, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task ToggleAllTopmostAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task SaveCurrentAsPresetAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task DeletePresetAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task RenamePresetAsync(string oldName, string newName, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task RestorePresetAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
    public void NotifyItemSelectionTaken(System.Windows.Window source) => throw new NotSupportedException();
    public void NotifyWormholeFocused(System.Windows.Window source) => throw new NotSupportedException();
    public void RequestSourceRecovery(WormholeRecord record) => throw new NotSupportedException();
    public void RelinkSource(WormholeRecord record, string newSourcePath) => throw new NotSupportedException();
    public Task<IReadOnlyList<WormholeRecord>> MissingSourcesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<int> RelinkMissingSourcesAsync(string newBaseFolder, CancellationToken cancellationToken) => throw new NotSupportedException();
    public void PauseWatchers() => throw new NotSupportedException();
    public void ResumeWatchers() => throw new NotSupportedException();
    public IReadOnlyList<IntPtr> LiveWindowHandles() => throw new NotSupportedException();
}
