using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using AresToys.Capture;
using AresToys.Storage.Paths;

namespace AresToys.App.Services.Wormholes;

/// <summary>JSON store for wormhole records, split across files so a different monitor
/// configuration (RDP, plug-in display, phone screen) can have its own layout without
/// corrupting the user's home-setup layout.
///
/// <para>Layout on disk:</para>
/// <code>
/// %LocalAppData%\AresToys-Data\Wormholes\
///   wormholes.json                            ← definitions + global flags (NO geometry)
///   Shortcuts\&lt;guid&gt;\                         ← Data-fence .lnk files (owned by DataDropPolicy)
///   Positions\
///     .original                               ← single-line text: hash of the "original" setup
///     &lt;setupHash&gt;.json                        ← per-setup geometry: {id → {X,Y,W,H,UnrolledHeight}}
/// </code>
///
/// <para>The "original" setup is whichever setup is active the first time the per-setup
/// feature loads. Its <c>Positions\&lt;hash&gt;.json</c> is never overwritten by switches —
/// when a new (never-seen) setup is detected we clamp the original's coords into the new
/// virtual screen and save that as a NEW file. Reconnecting to the original setup restores
/// the layout from its untouched file.</para>
///
/// <para>In-memory: <see cref="WormholeRecord.Geometry"/> mirrors whichever setup is currently
/// active. <see cref="SwitchSetupAsync"/> mutates those Geometry instances and returns the
/// snapshot to the caller (who pushes Left/Top/Width/Height onto the WPF windows).</para></summary>
public sealed class WormholeStoreJson : IWormholeStore, IDisposable
{
    private const string WormholesFolderName = "Wormholes";
    private const string ShortcutsFolderName = "Shortcuts";
    private const string PositionsFolderName = "Positions";
    private const string StoreFileName = "wormholes.json";
    private const string OriginalMarkerFileName = ".original";

    // Read-side options accept legacy JSON that still carries the per-record geometry block
    // (pre-multi-setup files) — JsonSerializer naturally ignores extra fields, but more
    // importantly the WormholeRecord type itself still owns a Geometry property at runtime,
    // so legacy values come back populated and we use them as the migration template.
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    // Write-side options strip the per-record geometry — definitions live in wormholes.json,
    // geometry lives in Positions\<hash>.json. Using a type-info resolver modifier is the
    // System.Text.Json idiomatic way to omit a single property on serialize without forcing
    // a second DTO type and a manual mapping layer; the read path still picks up Geometry
    // when present so legacy files migrate cleanly.
    private static readonly JsonSerializerOptions WriteDefinitionOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { StripGeometryOnWrite },
        },
    };

    private static readonly JsonSerializerOptions PositionsOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static void StripGeometryOnWrite(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type != typeof(WormholeRecord)) return;
        for (var i = typeInfo.Properties.Count - 1; i >= 0; i--)
        {
            if (string.Equals(typeInfo.Properties[i].Name, "geometry", StringComparison.OrdinalIgnoreCase))
            {
                typeInfo.Properties.RemoveAt(i);
            }
        }
    }

    private readonly IStoragePathResolver _paths;
    private readonly ILogger<WormholeStoreJson> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<WormholeRecord>? _cache;
    private string _currentSetupHash = string.Empty;

    public WormholeStoreJson(IStoragePathResolver paths, ILogger<WormholeStoreJson> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public string WormholesRootPath => Path.Combine(_paths.ResolveRoot(), WormholesFolderName);

    private string StoreFilePath => Path.Combine(WormholesRootPath, StoreFileName);
    private string PositionsRootPath => Path.Combine(WormholesRootPath, PositionsFolderName);
    private string PositionsFilePath(string setupHash) => Path.Combine(PositionsRootPath, setupHash + ".json");
    private string OriginalMarkerPath => Path.Combine(PositionsRootPath, OriginalMarkerFileName);

    public string CurrentSetupHash => _currentSetupHash;

    public string GetShortcutsDirectory(Guid wormholeId)
    {
        var dir = Path.Combine(WormholesRootPath, ShortcutsFolderName, wormholeId.ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public async Task<IReadOnlyList<WormholeRecord>> LoadAllAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCacheLoadedNoLockAsync(cancellationToken).ConfigureAwait(false);
            return _cache!.AsReadOnly();
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(WormholeRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCacheLoadedNoLockAsync(cancellationToken).ConfigureAwait(false);
            var idx = _cache!.FindIndex(r => r.Id == record.Id);
            if (idx >= 0) _cache[idx] = record;
            else _cache.Add(record);
            await FlushNoLockAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCacheLoadedNoLockAsync(cancellationToken).ConfigureAwait(false);
            await FlushNoLockAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<int> MoveAsync(Guid wormholeId, int delta, CancellationToken cancellationToken)
    {
        if (delta == 0) return -1;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCacheLoadedNoLockAsync(cancellationToken).ConfigureAwait(false);
            var idx = _cache!.FindIndex(r => r.Id == wormholeId);
            if (idx < 0) return -1;
            var newIdx = Math.Clamp(idx + delta, 0, _cache.Count - 1);
            if (newIdx == idx) return idx;
            var rec = _cache[idx];
            _cache.RemoveAt(idx);
            _cache.Insert(newIdx, rec);
            await FlushNoLockAsync(cancellationToken).ConfigureAwait(false);
            return newIdx;
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(Guid wormholeId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCacheLoadedNoLockAsync(cancellationToken).ConfigureAwait(false);
            var removed = _cache!.RemoveAll(r => r.Id == wormholeId);
            if (removed == 0) return;

            // Clean up the wormhole's Shortcuts\{id}\ folder if it exists. Best-effort — a
            // locked file (e.g. AV scan in progress) shouldn't block the record deletion in
            // memory + JSON; the user can clean leftover .lnk files manually.
            try
            {
                var shortcuts = Path.Combine(WormholesRootPath, ShortcutsFolderName, wormholeId.ToString("N"));
                if (Directory.Exists(shortcuts)) Directory.Delete(shortcuts, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove shortcuts folder for wormhole {Id}", wormholeId);
            }

            await FlushNoLockAsync(cancellationToken).ConfigureAwait(false);

            // Sweep the deleted id out of every per-setup positions file too — otherwise the
            // entry lingers as orphan JSON forever. Best-effort per file: if a setup file is
            // unreadable we skip it; the orphan is harmless (no record id matches at load).
            await RemovePositionEverywhereNoLockAsync(wormholeId, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyDictionary<Guid, WormholeGeometry>> SwitchSetupAsync(string newSetupHash, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(newSetupHash);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCacheLoadedNoLockAsync(cancellationToken).ConfigureAwait(false);
            if (string.Equals(_currentSetupHash, newSetupHash, StringComparison.Ordinal))
                return EmptyGeometryMap;

            // Load positions for the target setup. Three cases:
            //  a) File exists → use it as-is. The setup was seen before, layout is preserved.
            //  b) File missing AND original marker exists → clone from the original (clamped
            //     to the current virtual screen) so the user lands in a usable layout instead
            //     of every wormhole starting at the same default coords.
            //  c) File missing AND no marker → treat this setup as the new original (covers
            //     the post-upgrade boot case where a user changes monitor BEFORE the first
            //     save flushes the original marker).
            Directory.CreateDirectory(PositionsRootPath);
            var targetFile = PositionsFilePath(newSetupHash);
            WormholePositionsFile? loaded = null;
            if (File.Exists(targetFile))
            {
                loaded = await TryReadPositionsFileAsync(targetFile, cancellationToken).ConfigureAwait(false);
            }

            if (loaded is null)
            {
                var originalHash = await TryReadOriginalMarkerAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(originalHash) && originalHash != newSetupHash)
                {
                    var originalFile = PositionsFilePath(originalHash);
                    var originalPositions = await TryReadPositionsFileAsync(originalFile, cancellationToken).ConfigureAwait(false);
                    loaded = ClonePositionsClampedToVirtualScreen(originalPositions);
                }
                else
                {
                    // No reference template — start from whatever the records currently hold.
                    // First run after the multi-setup upgrade lands here; the migration step
                    // in EnsureCacheLoadedNoLockAsync will have stamped .original already.
                    loaded = SnapshotGeometriesFromCache();
                }

                // Persist the freshly-computed positions so the next switch hit reads them
                // straight from disk (and so a manual delete of wormholes.json doesn't lose
                // the user's per-setup layout).
                await WritePositionsFileNoLockAsync(newSetupHash, loaded, cancellationToken).ConfigureAwait(false);
            }

            // Apply the loaded geometry to each cached record in-place. Records with no entry
            // (newly-created wormhole the user hasn't seen on this setup yet) keep their
            // current in-memory geometry — that's whatever they had on the previous setup,
            // which is at least a usable starting point.
            var snapshot = new Dictionary<Guid, WormholeGeometry>(_cache!.Count);
            foreach (var rec in _cache)
            {
                if (loaded.Positions.TryGetValue(rec.Id, out var g))
                {
                    rec.Geometry.X = g.X;
                    rec.Geometry.Y = g.Y;
                    rec.Geometry.Width = g.Width;
                    rec.Geometry.Height = g.Height;
                    rec.Geometry.UnrolledHeight = g.UnrolledHeight;
                    rec.Geometry.MonitorId = g.MonitorId;
                }
                snapshot[rec.Id] = CloneGeometry(rec.Geometry);
            }

            _currentSetupHash = newSetupHash;
            _logger.LogInformation("Wormholes: active setup switched to {Hash} ({Count} record positions applied)", newSetupHash, snapshot.Count);
            return snapshot;
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureCacheLoadedNoLockAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null) return;
        Directory.CreateDirectory(WormholesRootPath);
        Directory.CreateDirectory(PositionsRootPath);

        // Step 1 — load record definitions + (legacy) embedded geometry from wormholes.json.
        // The Geometry property on each record is still populated at runtime; the layered file
        // step below either overrides it from the per-setup positions file or — on first-run
        // migration — uses it as the template that founds the original setup.
        if (!File.Exists(StoreFilePath))
        {
            _cache = new List<WormholeRecord>();
        }
        else
        {
            try
            {
                var raw = await File.ReadAllTextAsync(StoreFilePath, cancellationToken).ConfigureAwait(false);
                _cache = JsonSerializer.Deserialize<WormholeStoreFile>(raw, ReadOptions)?.Wormholes
                         ?? new List<WormholeRecord>();
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "wormholes.json is malformed — renaming aside and starting fresh");
                try { File.Move(StoreFilePath, StoreFilePath + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: false); }
                catch (IOException) { /* best-effort */ }
                _cache = new List<WormholeRecord>();
            }
        }

        // Step 2 — pick the current monitor configuration as the active setup hash.
        _currentSetupHash = MonitorSetupIdentifier.ComputeCurrentSetupHash();

        // Step 3 — layered positions file. If one exists for this setup hash, override the
        // per-record geometry with it (the setup file is the source of truth post-migration).
        // If none exists, run the migration / template-clone flow.
        var setupFile = PositionsFilePath(_currentSetupHash);
        if (File.Exists(setupFile))
        {
            var loaded = await TryReadPositionsFileAsync(setupFile, cancellationToken).ConfigureAwait(false);
            if (loaded is not null) ApplyPositionsToCache(loaded);
        }
        else
        {
            await MigrateOrCloneOnFirstHitNoLockAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>First-run handler for a setup we haven't persisted positions for yet. Two
    /// branches:
    /// <list type="bullet">
    /// <item>No <c>.original</c> marker: this is the very first launch with the multi-setup
    /// feature. Stamp the current setup as the original and snapshot the records' Geometry
    /// (which came from the legacy wormholes.json) as the original positions file.</item>
    /// <item>Marker exists but no file for this hash: a brand-new setup (e.g. first RDP
    /// connect). Clone the original's positions, clamp to the current virtual screen, and
    /// write as the new setup file. Original file stays untouched.</item>
    /// </list></summary>
    private async Task MigrateOrCloneOnFirstHitNoLockAsync(CancellationToken cancellationToken)
    {
        var originalHash = await TryReadOriginalMarkerAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(originalHash))
        {
            // First run with the feature. Promote the current setup to "original" and snapshot
            // whatever geometries the records carry (legacy wormholes.json or empty defaults).
            var snapshot = SnapshotGeometriesFromCache();
            await WritePositionsFileNoLockAsync(_currentSetupHash, snapshot, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(OriginalMarkerPath, _currentSetupHash, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Wormholes: marked setup {Hash} as the original layout (first-run migration)", _currentSetupHash);
            return;
        }

        // Marker present: clone the original's geometry into a fresh per-setup file. Clamp
        // every position to the current virtual screen so the wormholes are at least visible
        // on the smaller / repositioned displays.
        var originalFile = PositionsFilePath(originalHash);
        var originalPositions = await TryReadPositionsFileAsync(originalFile, cancellationToken).ConfigureAwait(false);
        var cloned = ClonePositionsClampedToVirtualScreen(originalPositions);
        ApplyPositionsToCache(cloned);
        await WritePositionsFileNoLockAsync(_currentSetupHash, cloned, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Wormholes: cloned original layout {OrigHash} → {NewHash} for new monitor setup", originalHash, _currentSetupHash);
    }

    private WormholePositionsFile SnapshotGeometriesFromCache()
    {
        var file = new WormholePositionsFile { SchemaVersion = 1 };
        foreach (var rec in _cache!)
        {
            file.Positions[rec.Id] = CloneGeometry(rec.Geometry);
        }
        return file;
    }

    private void ApplyPositionsToCache(WormholePositionsFile positions)
    {
        foreach (var rec in _cache!)
        {
            if (!positions.Positions.TryGetValue(rec.Id, out var g)) continue;
            rec.Geometry.X = g.X;
            rec.Geometry.Y = g.Y;
            rec.Geometry.Width = g.Width;
            rec.Geometry.Height = g.Height;
            rec.Geometry.UnrolledHeight = g.UnrolledHeight;
            rec.Geometry.MonitorId = g.MonitorId;
        }
    }

    /// <summary>Clamp every entry of <paramref name="source"/> into the current virtual screen
    /// so the cloned layout is usable on the new (typically smaller) display. Returns a new
    /// <see cref="WormholePositionsFile"/> — the input is left untouched, so the same
    /// reference can serve multiple destination setups without aliasing bugs.</summary>
    private static WormholePositionsFile ClonePositionsClampedToVirtualScreen(WormholePositionsFile? source)
    {
        var result = new WormholePositionsFile { SchemaVersion = 1 };
        if (source is null) return result;

        var (vsLeft, vsTop, vsW, vsH) = GetCurrentVirtualScreenBounds();
        foreach (var (id, g) in source.Positions)
        {
            var clamped = CloneGeometry(g);
            // Clamp width/height to fit if the new screen is smaller than the source coords.
            clamped.Width = Math.Min(g.Width, Math.Max(120, vsW - 40));
            var effHeight = Math.Min(g.Height, Math.Max(60, vsH - 40));
            clamped.Height = effHeight;
            // Snap top-left into the visible rect, leaving at least 40px of the chrome
            // visible on the right/bottom edges (matches WormholeWindowManager's off-screen
            // recovery threshold so the user can always grab the chrome to move it).
            clamped.X = Math.Clamp(g.X, vsLeft, vsLeft + Math.Max(0, vsW - 40));
            clamped.Y = Math.Clamp(g.Y, vsTop, vsTop + Math.Max(0, vsH - 40));
            result.Positions[id] = clamped;
        }
        return result;
    }

    /// <summary>Bounds of the current virtual screen in physical pixels, derived from the
    /// live monitor enumeration. Wrap because the WPF SystemParameters access would require
    /// a UI-thread hop and this store is called from background tasks (save handlers fired
    /// off LocationChanged on the dispatcher, but the locked code runs on whatever pool
    /// thread System.Text.Json resumes on).</summary>
    private static (int Left, int Top, int Width, int Height) GetCurrentVirtualScreenBounds()
    {
        var monitors = MonitorEnumeration.Enumerate();
        if (monitors.Count == 0) return (0, 0, 1920, 1080);
        var left = monitors.Min(m => m.X);
        var top = monitors.Min(m => m.Y);
        var right = monitors.Max(m => m.X + m.Width);
        var bottom = monitors.Max(m => m.Y + m.Height);
        return (left, top, right - left, bottom - top);
    }

    private async Task<string?> TryReadOriginalMarkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(OriginalMarkerPath)) return null;
            var text = await File.ReadAllTextAsync(OriginalMarkerPath, cancellationToken).ConfigureAwait(false);
            return text.Trim();
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not read original-setup marker");
            return null;
        }
    }

    private async Task<WormholePositionsFile?> TryReadPositionsFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var raw = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<WormholePositionsFile>(raw, PositionsOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning(ex, "Positions file {Path} unreadable — treating as missing", path);
            return null;
        }
    }

    private async Task WritePositionsFileNoLockAsync(string setupHash, WormholePositionsFile contents, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(PositionsRootPath);
        var path = PositionsFilePath(setupHash);
        var json = JsonSerializer.Serialize(contents, PositionsOptions);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, json, cancellationToken).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Strip the wormhole id from every per-setup positions file. Called from
    /// <see cref="DeleteAsync"/> so the deleted record doesn't linger as orphan JSON keys
    /// the next time the user revisits an older setup. Best-effort per file — one unreadable
    /// or locked positions file doesn't block the rest.</summary>
    private async Task RemovePositionEverywhereNoLockAsync(Guid wormholeId, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(PositionsRootPath)) return;
        foreach (var file in Directory.EnumerateFiles(PositionsRootPath, "*.json"))
        {
            try
            {
                var positions = await TryReadPositionsFileAsync(file, cancellationToken).ConfigureAwait(false);
                if (positions is null) continue;
                if (!positions.Positions.Remove(wormholeId)) continue;
                var setupHash = Path.GetFileNameWithoutExtension(file);
                await WritePositionsFileNoLockAsync(setupHash, positions, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to prune deleted wormhole {Id} from positions file {File}", wormholeId, file);
            }
        }
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>Writes the current cache to <c>wormholes.json</c> (definitions + global flags,
    /// no geometry — see <see cref="WriteDefinitionOptions"/>) AND mirrors the current Geometry
    /// of every record into the active setup's positions file. Both writes go through the temp-
    /// rename atomic-replace pattern so a crash mid-save never leaves a half-written file.</summary>
    private async Task FlushNoLockAsync(CancellationToken cancellationToken)
    {
        var file = new WormholeStoreFile { SchemaVersion = 2, Wormholes = _cache! };
        var json = JsonSerializer.Serialize(file, WriteDefinitionOptions);
        Directory.CreateDirectory(WormholesRootPath);
        var tmp = StoreFilePath + ".tmp";
        await File.WriteAllTextAsync(tmp, json, cancellationToken).ConfigureAwait(false);
        File.Move(tmp, StoreFilePath, overwrite: true);

        // Mirror the live geometries into the active setup's positions file. Done after the
        // definitions write so a partial failure leaves the definitions consistent (geometry
        // is recoverable from the next user interaction; definitions aren't).
        if (!string.IsNullOrEmpty(_currentSetupHash))
        {
            var positions = SnapshotGeometriesFromCache();
            await WritePositionsFileNoLockAsync(_currentSetupHash, positions, cancellationToken).ConfigureAwait(false);
        }
    }

    private static WormholeGeometry CloneGeometry(WormholeGeometry source)
        => new()
        {
            X = source.X,
            Y = source.Y,
            Width = source.Width,
            Height = source.Height,
            UnrolledHeight = source.UnrolledHeight,
            MonitorId = source.MonitorId,
        };

    private static readonly IReadOnlyDictionary<Guid, WormholeGeometry> EmptyGeometryMap
        = new Dictionary<Guid, WormholeGeometry>();
}
