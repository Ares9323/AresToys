namespace AresToys.App.Services.Wormholes;

/// <summary>Read / write contract for the wormholes JSON store. Lives in
/// <c>%LocalAppData%\AresToys-Data\Wormholes\wormholes.json</c> with a sibling <c>Shortcuts\</c>
/// folder owned by <c>DataDropPolicy</c> (one subfolder per wormhole id) for Data-fence
/// <c>.lnk</c> files.</summary>
public interface IWormholeStore
{
    /// <summary>Hydrates the in-memory list from disk. Idempotent — safe to call multiple times,
    /// always re-reads the file. Returns an empty list if the file doesn't exist yet (first run
    /// after enabling the module).</summary>
    Task<IReadOnlyList<WormholeRecord>> LoadAllAsync(CancellationToken cancellationToken);

    /// <summary>Upserts the record (matched by <see cref="WormholeRecord.Id"/>) and flushes the
    /// whole file. Writes atomically via a temp-file rename so a crash mid-save never leaves a
    /// half-written JSON behind.</summary>
    Task SaveAsync(WormholeRecord record, CancellationToken cancellationToken);

    /// <summary>Flush the current in-memory cache (whatever the manager's mutated) to disk in
    /// one atomic temp-rename write. Used by batch operations like "Hide all" / "Show all" so
    /// the manager can mutate every record's flag in memory, then persist with a single JSON
    /// write instead of N — which is what made batch ops feel gradual: each per-record
    /// SaveAsync flushed the whole file separately, serialized through the store's semaphore.</summary>
    Task FlushAsync(CancellationToken cancellationToken);

    /// <summary>Removes the record and its <c>Shortcuts\{id}\</c> folder (Data fences) or just
    /// the record (Portal fences — the watched source folder isn't ours to touch). Safe to call
    /// for an id that doesn't exist (no-op).</summary>
    Task DeleteAsync(Guid wormholeId, CancellationToken cancellationToken);

    /// <summary>Shift the record with id <paramref name="wormholeId"/> by <paramref name="delta"/>
    /// positions in the persisted list (-1 = move up, +1 = move down). Clamps at list bounds —
    /// trying to move the first record up or the last one down is a no-op. Flushes the file on
    /// success. Returns the new index (or -1 if the id wasn't found).</summary>
    Task<int> MoveAsync(Guid wormholeId, int delta, CancellationToken cancellationToken);

    /// <summary>Resolves the absolute path to <c>Shortcuts\{wormholeId}\</c>. Used by
    /// <c>DataDropPolicy</c> when materialising new <c>.lnk</c> files on drop. The folder is
    /// created on demand; safe to call before the wormhole has any items.</summary>
    string GetShortcutsDirectory(Guid wormholeId);

    /// <summary>Absolute path to the root <c>Wormholes\</c> folder. Exposed so callers (e.g. a
    /// future backup-import path) can address the folder as a unit.</summary>
    string WormholesRootPath { get; }

    /// <summary>Absolute path to the <c>Presets\</c> folder holding one JSON per named layout
    /// preset. May not exist yet if no preset was ever saved; callers that open it should create
    /// it on demand.</summary>
    string PresetsFolderPath { get; }

    // ------------------------------------------------------------------------------------------
    // Named layout presets + per-setup auto-apply map. Replaces the old per-monitor-hash layout
    // files: instead of the store silently cloning + clamping a layout for every new monitor
    // configuration (which destroyed the home layout over RDP), the user saves named presets and
    // the store maps each known monitor fingerprint to the preset that should reapply for it.
    // Unknown fingerprints (RDP) map to nothing: the live layout is left untouched.
    // ------------------------------------------------------------------------------------------

    /// <summary>Names of all saved presets, in insertion order. Empty when none exist.</summary>
    Task<IReadOnlyList<string>> ListPresetNamesAsync(CancellationToken cancellationToken);

    /// <summary>Snapshot the CURRENT live geometry of every record into a preset named
    /// <paramref name="name"/> (creating it, or overwriting an existing one with the same name,
    /// case-insensitive) AND associate the current monitor fingerprint with it so it auto-applies
    /// when this setup returns. Whitespace-only names are rejected (ArgumentException).</summary>
    Task SavePresetAsync(string name, CancellationToken cancellationToken);

    /// <summary>Delete the preset named <paramref name="name"/> and drop every setup-map entry
    /// pointing at it. No-op if it doesn't exist.</summary>
    Task DeletePresetAsync(string name, CancellationToken cancellationToken);

    /// <summary>Rename a preset, re-pointing any setup-map entries. No-op if <paramref name="oldName"/>
    /// is missing; rejects a whitespace or colliding <paramref name="newName"/>.</summary>
    Task RenamePresetAsync(string oldName, string newName, CancellationToken cancellationToken);

    /// <summary>The stored geometry of the preset named <paramref name="name"/>, or null if it
    /// doesn't exist. Read-only snapshot for a restore.</summary>
    Task<IReadOnlyDictionary<Guid, WormholeGeometry>?> GetPresetPositionsAsync(string name, CancellationToken cancellationToken);

    /// <summary>The stored per-wormhole hidden/locked/rolled state of the preset named
    /// <paramref name="name"/>. Empty when the preset doesn't exist or predates state capture
    /// (in which case the caller leaves those flags untouched on restore).</summary>
    Task<IReadOnlyDictionary<Guid, WormholePresetState>> GetPresetStatesAsync(string name, CancellationToken cancellationToken);

    /// <summary>Point the current monitor fingerprint at the preset named <paramref name="name"/>
    /// so it auto-applies for this setup from now on. Called after a manual Restore.</summary>
    Task AssociateCurrentSetupAsync(string name, CancellationToken cancellationToken);

    /// <summary>Name of the preset mapped to the CURRENT monitor fingerprint, or null when the
    /// current setup is unknown (nothing to auto-apply — e.g. an RDP resolution never saved).</summary>
    Task<string?> GetPresetNameForCurrentSetupAsync(CancellationToken cancellationToken);

    /// <summary>Push <paramref name="positions"/> into the matching records' <c>Geometry</c>
    /// in-place (ids not present in the cache are ignored) and flush <c>positions.json</c>. Used
    /// by restore / auto-apply. Records absent from the map keep their current geometry.</summary>
    Task ApplyPositionsAsync(IReadOnlyDictionary<Guid, WormholeGeometry> positions, CancellationToken cancellationToken);
}
