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
}
