using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using AresToys.Storage.Paths;

namespace AresToys.App.Services.Wormholes;

/// <summary>JSON store for wormhole records and their layout.
///
/// <para>Layout on disk:</para>
/// <code>
/// %LocalAppData%\AresToys-Data\Wormholes\
///   wormholes.json                            ← definitions + global flags (NO geometry)
///   positions.json                            ← current live geometry {id → {X,Y,W,H,UnrolledHeight}}
///   Presets\&lt;name&gt;.json                       ← one file per named layout preset (hand-editable)
///   Shortcuts\&lt;guid&gt;\                         ← Data-fence .lnk files (owned by DataDropPolicy)
/// </code>
///
/// <para>Each preset file carries its own geometry + per-wormhole state (hidden/locked/rolled) and
/// the list of monitor fingerprints it auto-applies for, so a preset is fully self-contained: the
/// user can delete or edit one in Explorer without touching the others. Unknown setups (RDP) map
/// to no preset and never overwrite a good layout. The old opaque per-monitor-hash positions files
/// (with clamping) are gone; a one-time migration lifts them into positions.json + an "Original"
/// preset, and a single-file <c>presets.json</c> from the interim build is split into per-preset
/// files.</para></summary>
public sealed class WormholeStoreJson : IWormholeStore, IDisposable
{
    private const string WormholesFolderName = "Wormholes";
    private const string ShortcutsFolderName = "Shortcuts";
    private const string StoreFileName = "wormholes.json";
    private const string PositionsFileName = "positions.json";
    private const string PresetsFolderName = "Presets";
    private const string LegacyPositionsFolderName = "Positions";
    private const string LegacyOriginalMarkerFileName = ".original";
    private const string LegacyPresetsFileName = "presets.json";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

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

    public WormholeStoreJson(IStoragePathResolver paths, ILogger<WormholeStoreJson> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public string WormholesRootPath => Path.Combine(_paths.ResolveRoot(), WormholesFolderName);

    public string PresetsFolderPath => PresetsRootPath;

    private string StoreFilePath => Path.Combine(WormholesRootPath, StoreFileName);
    private string PositionsFilePath => Path.Combine(WormholesRootPath, PositionsFileName);
    private string PresetsRootPath => Path.Combine(WormholesRootPath, PresetsFolderName);
    private string LegacyPositionsRootPath => Path.Combine(WormholesRootPath, LegacyPositionsFolderName);
    private string LegacyPresetsFilePath => Path.Combine(WormholesRootPath, LegacyPresetsFileName);

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

            // Sweep the deleted id out of every preset file so it doesn't linger as orphan JSON.
            foreach (var (path, preset) in await LoadAllPresetsNoLockAsync(cancellationToken).ConfigureAwait(false))
            {
                var touched = preset.Positions.Remove(wormholeId);
                touched |= preset.States.Remove(wormholeId);
                if (touched) await WritePresetFileNoLockAsync(path, preset, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }

    // ------------------------------------------------------------------------------------------
    // Preset API — one file per preset under Presets\.
    // ------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<string>> ListPresetNamesAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await LoadAllPresetsNoLockAsync(cancellationToken).ConfigureAwait(false))
                .Select(p => p.Preset.Name).ToList();
        }
        finally { _gate.Release(); }
    }

    public async Task SavePresetAsync(string name, CancellationToken cancellationToken)
    {
        var clean = (name ?? string.Empty).Trim();
        if (clean.Length == 0) throw new ArgumentException("Preset name cannot be empty.", nameof(name));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCacheLoadedNoLockAsync(cancellationToken).ConfigureAwait(false);
            var hash = MonitorSetupIdentifier.ComputeCurrentSetupHash();
            var all = await LoadAllPresetsNoLockAsync(cancellationToken).ConfigureAwait(false);

            var snapshot = new Dictionary<Guid, WormholeGeometry>(_cache!.Count);
            var states = new Dictionary<Guid, WormholePresetState>(_cache.Count);
            foreach (var rec in _cache)
            {
                snapshot[rec.Id] = CloneGeometry(rec.Geometry);
                states[rec.Id] = new WormholePresetState { Hidden = rec.IsHidden, Locked = rec.IsLocked, Rolled = rec.IsRolled };
            }

            var match = all.FirstOrDefault(p => string.Equals(p.Preset.Name, clean, StringComparison.OrdinalIgnoreCase));
            WormholePreset preset;
            string path;
            if (match.Preset is not null)
            {
                preset = match.Preset;
                path = match.Path;
            }
            else
            {
                preset = new WormholePreset { Name = clean };
                path = UniquePresetPathNoLock(clean, all.Select(p => p.Path));
            }
            preset.Positions = snapshot;
            preset.States = states;
            if (!preset.Setups.Contains(hash, StringComparer.Ordinal)) preset.Setups.Add(hash);

            // A fingerprint maps to at most one preset: drop this setup from every OTHER preset.
            foreach (var (otherPath, other) in all)
            {
                if (ReferenceEquals(other, preset)) continue;
                if (other.Setups.RemoveAll(h => string.Equals(h, hash, StringComparison.Ordinal)) > 0)
                    await WritePresetFileNoLockAsync(otherPath, other, cancellationToken).ConfigureAwait(false);
            }

            await WritePresetFileNoLockAsync(path, preset, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task DeletePresetAsync(string name, CancellationToken cancellationToken)
    {
        var clean = (name ?? string.Empty).Trim();
        if (clean.Length == 0) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var match = (await LoadAllPresetsNoLockAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(p => string.Equals(p.Preset.Name, clean, StringComparison.OrdinalIgnoreCase));
            if (match.Preset is null) return;
            try { File.Delete(match.Path); }
            catch (IOException ex) { _logger.LogWarning(ex, "Failed to delete preset file {Path}", match.Path); }
        }
        finally { _gate.Release(); }
    }

    public async Task RenamePresetAsync(string oldName, string newName, CancellationToken cancellationToken)
    {
        var from = (oldName ?? string.Empty).Trim();
        var to = (newName ?? string.Empty).Trim();
        if (from.Length == 0) return;
        if (to.Length == 0) throw new ArgumentException("New preset name cannot be empty.", nameof(newName));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = await LoadAllPresetsNoLockAsync(cancellationToken).ConfigureAwait(false);
            var match = all.FirstOrDefault(p => string.Equals(p.Preset.Name, from, StringComparison.OrdinalIgnoreCase));
            if (match.Preset is null) return;
            if (all.Any(p => !ReferenceEquals(p.Preset, match.Preset) && string.Equals(p.Preset.Name, to, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"A preset named '{to}' already exists.", nameof(newName));

            match.Preset.Name = to;
            var newPath = UniquePresetPathNoLock(to, all.Where(p => !ReferenceEquals(p.Preset, match.Preset)).Select(p => p.Path));
            await WritePresetFileNoLockAsync(newPath, match.Preset, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(newPath, match.Path, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(match.Path); }
                catch (IOException ex) { _logger.LogWarning(ex, "Failed to remove old preset file {Path} after rename", match.Path); }
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyDictionary<Guid, WormholeGeometry>?> GetPresetPositionsAsync(string name, CancellationToken cancellationToken)
    {
        var clean = (name ?? string.Empty).Trim();
        if (clean.Length == 0) return null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var match = (await LoadAllPresetsNoLockAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(p => string.Equals(p.Preset.Name, clean, StringComparison.OrdinalIgnoreCase));
            if (match.Preset is null) return null;
            var copy = new Dictionary<Guid, WormholeGeometry>(match.Preset.Positions.Count);
            foreach (var (id, g) in match.Preset.Positions) copy[id] = CloneGeometry(g);
            return copy;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyDictionary<Guid, WormholePresetState>> GetPresetStatesAsync(string name, CancellationToken cancellationToken)
    {
        var clean = (name ?? string.Empty).Trim();
        var result = new Dictionary<Guid, WormholePresetState>();
        if (clean.Length == 0) return result;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var match = (await LoadAllPresetsNoLockAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(p => string.Equals(p.Preset.Name, clean, StringComparison.OrdinalIgnoreCase));
            if (match.Preset is null) return result;
            foreach (var (id, s) in match.Preset.States)
                result[id] = new WormholePresetState { Hidden = s.Hidden, Locked = s.Locked, Rolled = s.Rolled };
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task AssociateCurrentSetupAsync(string name, CancellationToken cancellationToken)
    {
        var clean = (name ?? string.Empty).Trim();
        if (clean.Length == 0) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var hash = MonitorSetupIdentifier.ComputeCurrentSetupHash();
            var all = await LoadAllPresetsNoLockAsync(cancellationToken).ConfigureAwait(false);
            var match = all.FirstOrDefault(p => string.Equals(p.Preset.Name, clean, StringComparison.OrdinalIgnoreCase));
            if (match.Preset is null) return;

            foreach (var (otherPath, other) in all)
            {
                if (ReferenceEquals(other, match.Preset)) continue;
                if (other.Setups.RemoveAll(h => string.Equals(h, hash, StringComparison.Ordinal)) > 0)
                    await WritePresetFileNoLockAsync(otherPath, other, cancellationToken).ConfigureAwait(false);
            }
            if (!match.Preset.Setups.Contains(hash, StringComparer.Ordinal))
            {
                match.Preset.Setups.Add(hash);
                await WritePresetFileNoLockAsync(match.Path, match.Preset, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<string?> GetPresetNameForCurrentSetupAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var hash = MonitorSetupIdentifier.ComputeCurrentSetupHash();
            var match = (await LoadAllPresetsNoLockAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(p => p.Preset.Setups.Contains(hash, StringComparer.Ordinal));
            return match.Preset?.Name;
        }
        finally { _gate.Release(); }
    }

    public async Task ApplyPositionsAsync(IReadOnlyDictionary<Guid, WormholeGeometry> positions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(positions);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCacheLoadedNoLockAsync(cancellationToken).ConfigureAwait(false);
            foreach (var rec in _cache!)
            {
                if (!positions.TryGetValue(rec.Id, out var g)) continue;
                rec.Geometry.X = g.X;
                rec.Geometry.Y = g.Y;
                rec.Geometry.Width = g.Width;
                rec.Geometry.Height = g.Height;
                rec.Geometry.UnrolledHeight = g.UnrolledHeight;
                rec.Geometry.MonitorId = g.MonitorId;
            }
            await FlushNoLockAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    // ------------------------------------------------------------------------------------------
    // Preset file helpers
    // ------------------------------------------------------------------------------------------

    private async Task<List<(string Path, WormholePreset Preset)>> LoadAllPresetsNoLockAsync(CancellationToken cancellationToken)
    {
        var list = new List<(string, WormholePreset)>();
        if (!Directory.Exists(PresetsRootPath)) return list;
        foreach (var file in Directory.EnumerateFiles(PresetsRootPath, "*.json"))
        {
            try
            {
                var raw = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                var preset = JsonSerializer.Deserialize<WormholePreset>(raw, PositionsOptions);
                if (preset is null) continue;
                if (string.IsNullOrWhiteSpace(preset.Name))
                    preset.Name = Path.GetFileNameWithoutExtension(file);
                list.Add((file, preset));
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                _logger.LogWarning(ex, "Preset file {Path} unreadable — skipping", file);
            }
        }
        return list;
    }

    private async Task WritePresetFileNoLockAsync(string path, WormholePreset preset, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(PresetsRootPath);
        var json = JsonSerializer.Serialize(preset, PositionsOptions);
        await WriteAtomicAsync(path, json, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Path for a new preset: <c>Presets\&lt;sanitized-name&gt;.json</c>, with a numeric
    /// suffix if that file is already taken by a different preset (name-to-filename collision).</summary>
    private string UniquePresetPathNoLock(string name, IEnumerable<string> existingPaths)
    {
        var taken = new HashSet<string>(existingPaths, StringComparer.OrdinalIgnoreCase);
        var stem = SanitizeFileName(name);
        var candidate = Path.Combine(PresetsRootPath, stem + ".json");
        if (!taken.Contains(candidate) && !File.Exists(candidate)) return candidate;
        for (var n = 2; n < 1000; n++)
        {
            candidate = Path.Combine(PresetsRootPath, $"{stem} ({n}).json");
            if (!taken.Contains(candidate) && !File.Exists(candidate)) return candidate;
        }
        return Path.Combine(PresetsRootPath, $"{stem}-{Guid.NewGuid():N}.json");
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        var s = sb.ToString().Trim().TrimEnd('.');
        return s.Length == 0 ? "preset" : s;
    }

    // ------------------------------------------------------------------------------------------
    // Load / flush of records + positions
    // ------------------------------------------------------------------------------------------

    private async Task EnsureCacheLoadedNoLockAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null) return;
        Directory.CreateDirectory(WormholesRootPath);

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

        // Geometry: positions.json is the source of truth. If absent, migrate from the legacy
        // per-hash Positions\ layout (one-time); else keep the legacy embedded geometry.
        if (File.Exists(PositionsFilePath))
        {
            var loaded = await TryReadPositionsFileAsync(PositionsFilePath, cancellationToken).ConfigureAwait(false);
            if (loaded is not null) ApplyPositionsToCache(loaded);
        }
        else if (Directory.Exists(LegacyPositionsRootPath))
        {
            await MigrateFromLegacyPositionsNoLockAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await WritePositionsFileNoLockAsync(SnapshotGeometriesFromCache(), cancellationToken).ConfigureAwait(false);
        }

        // Split a single-file presets.json (interim build) into per-preset files, one time.
        if (File.Exists(LegacyPresetsFilePath))
        {
            await MigrateLegacyPresetsFileNoLockAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MigrateFromLegacyPositionsNoLockAsync(CancellationToken cancellationToken)
    {
        try
        {
            var currentHash = MonitorSetupIdentifier.ComputeCurrentSetupHash();
            var currentFile = Path.Combine(LegacyPositionsRootPath, currentHash + ".json");

            string? originalHash = null;
            var marker = Path.Combine(LegacyPositionsRootPath, LegacyOriginalMarkerFileName);
            if (File.Exists(marker))
            {
                try { originalHash = (await File.ReadAllTextAsync(marker, cancellationToken).ConfigureAwait(false)).Trim(); }
                catch (IOException) { /* best-effort */ }
            }

            var current = await TryReadPositionsFileAsync(currentFile, cancellationToken).ConfigureAwait(false);
            WormholePositionsFile? original = null;
            if (!string.IsNullOrEmpty(originalHash))
                original = await TryReadPositionsFileAsync(Path.Combine(LegacyPositionsRootPath, originalHash + ".json"), cancellationToken).ConfigureAwait(false);

            var live = current ?? original;
            if (live is not null) ApplyPositionsToCache(live);
            await WritePositionsFileNoLockAsync(live ?? SnapshotGeometriesFromCache(), cancellationToken).ConfigureAwait(false);

            if (original is not null && original.Positions.Count > 0 && !string.IsNullOrEmpty(originalHash))
            {
                var pos = new Dictionary<Guid, WormholeGeometry>(original.Positions.Count);
                foreach (var (id, g) in original.Positions) pos[id] = CloneGeometry(g);
                var st = new Dictionary<Guid, WormholePresetState>();
                foreach (var rec in _cache!) st[rec.Id] = new WormholePresetState { Hidden = rec.IsHidden, Locked = rec.IsLocked, Rolled = rec.IsRolled };
                var preset = new WormholePreset { Name = "Original", Positions = pos, States = st, Setups = { originalHash } };
                await WritePresetFileNoLockAsync(UniquePresetPathNoLock("Original", Array.Empty<string>()), preset, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                var aside = LegacyPositionsRootPath + $".migrated-{DateTime.UtcNow:yyyyMMddHHmmss}";
                Directory.Move(LegacyPositionsRootPath, aside);
            }
            catch (IOException ex) { _logger.LogWarning(ex, "Could not archive legacy Positions folder"); }

            _logger.LogInformation("Wormholes: migrated legacy per-setup positions to positions.json + Presets\\");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Wormholes: legacy positions migration failed; starting from record geometry");
            await WritePositionsFileNoLockAsync(SnapshotGeometriesFromCache(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Split the interim single-file <c>presets.json</c> (a Presets list + a
    /// fingerprint→name SetupMap) into one <c>Presets\&lt;name&gt;.json</c> per preset, then rename
    /// the old file aside.</summary>
    private async Task MigrateLegacyPresetsFileNoLockAsync(CancellationToken cancellationToken)
    {
        try
        {
            var raw = await File.ReadAllTextAsync(LegacyPresetsFilePath, cancellationToken).ConfigureAwait(false);
            var legacy = JsonSerializer.Deserialize<LegacyPresetsFile>(raw, PositionsOptions);
            if (legacy?.Presets is { Count: > 0 })
            {
                var takenPaths = new List<string>();
                foreach (var p in legacy.Presets)
                {
                    if (string.IsNullOrWhiteSpace(p.Name)) continue;
                    var setups = legacy.SetupMap
                        .Where(kv => string.Equals(kv.Value, p.Name, StringComparison.OrdinalIgnoreCase))
                        .Select(kv => kv.Key).ToList();
                    var preset = new WormholePreset { Name = p.Name, Positions = p.Positions, States = p.States, Setups = setups };
                    var path = UniquePresetPathNoLock(p.Name, takenPaths);
                    takenPaths.Add(path);
                    await WritePresetFileNoLockAsync(path, preset, cancellationToken).ConfigureAwait(false);
                }
            }
            try { File.Move(LegacyPresetsFilePath, LegacyPresetsFilePath + $".migrated-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: false); }
            catch (IOException) { /* best-effort */ }
            _logger.LogInformation("Wormholes: split legacy presets.json into per-preset files");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Wormholes: presets.json split migration failed");
        }
    }

    private WormholePositionsFile SnapshotGeometriesFromCache()
    {
        var file = new WormholePositionsFile { SchemaVersion = 1 };
        foreach (var rec in _cache!) file.Positions[rec.Id] = CloneGeometry(rec.Geometry);
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

    private async Task FlushNoLockAsync(CancellationToken cancellationToken)
    {
        var file = new WormholeStoreFile { SchemaVersion = 2, Wormholes = _cache! };
        var json = JsonSerializer.Serialize(file, WriteDefinitionOptions);
        Directory.CreateDirectory(WormholesRootPath);
        await WriteAtomicAsync(StoreFilePath, json, cancellationToken).ConfigureAwait(false);
        await WritePositionsFileNoLockAsync(SnapshotGeometriesFromCache(), cancellationToken).ConfigureAwait(false);
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

    private async Task WritePositionsFileNoLockAsync(WormholePositionsFile contents, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(contents, PositionsOptions);
        await WriteAtomicAsync(PositionsFilePath, json, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteAtomicAsync(string path, string json, CancellationToken cancellationToken)
    {
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, json, cancellationToken).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }

    public void Dispose() => _gate.Dispose();

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

    /// <summary>Shape of the interim single-file <c>presets.json</c>, read only during the one-time
    /// split into per-preset files. Not written anymore.</summary>
    private sealed class LegacyPresetsFile
    {
        public List<LegacyPreset> Presets { get; set; } = new();
        public Dictionary<string, string> SetupMap { get; set; } = new();
    }

    private sealed class LegacyPreset
    {
        public string Name { get; set; } = string.Empty;
        public Dictionary<Guid, WormholeGeometry> Positions { get; set; } = new();
        public Dictionary<Guid, WormholePresetState> States { get; set; } = new();
    }
}
