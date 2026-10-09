using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using AresToys.Storage.Paths;

namespace AresToys.App.Services.Pins;

/// <summary>On-disk state of the pinned windows that survive a restart.
///
/// <code>
/// %LocalAppData%\AresToys-Data\Pins\
///   pins.json          manifest: one <see cref="PinRecord"/> per open pin
///   &lt;guid&gt;.png         image pins, written once at pin time (lossless, alpha kept)
/// </code>
///
/// Video pins reference their file where it already is. Every method is synchronous and
/// serialized by one lock: the files are tiny and callers run them off the UI thread.</summary>
public sealed class PinStore
{
    public const string FolderName = "Pins";
    public const string ManifestFileName = "pins.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IStoragePathResolver _paths;
    private readonly ILogger<PinStore> _logger;
    private readonly object _gate = new();
    private List<PinRecord>? _cache;

    public PinStore(IStoragePathResolver paths, ILogger<PinStore> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public string FolderPath => Path.Combine(_paths.ResolveRoot(), FolderName);

    private string ManifestPath => Path.Combine(FolderPath, ManifestFileName);

    public static string ImageFileNameFor(Guid id) => id.ToString("N") + ".png";

    /// <summary>Read the manifest for a restore and prune it: entries whose PNG (or video file)
    /// is gone are dropped with a log line, and PNGs no entry points at are deleted. Returns
    /// copies, in pin order.</summary>
    public IReadOnlyList<PinRecord> LoadAndPrune()
    {
        lock (_gate)
        {
            var list = EnsureLoadedNoLock();
            var seen = new HashSet<Guid>();
            var removed = list.RemoveAll(r =>
            {
                if (!seen.Add(r.Id))
                {
                    _logger.LogWarning("Pins: duplicate entry {Id} dropped", r.Id);
                    return true;
                }
                if (r.Kind == PinKind.Image)
                {
                    if (string.IsNullOrEmpty(r.FileName) || !IsPlainFileName(r.FileName)
                        || !File.Exists(Path.Combine(FolderPath, r.FileName)))
                    {
                        _logger.LogWarning("Pins: image file {File} for pin {Id} is missing, entry pruned", r.FileName, r.Id);
                        return true;
                    }
                }
                else if (string.IsNullOrEmpty(r.SourcePath) || !File.Exists(r.SourcePath))
                {
                    _logger.LogWarning("Pins: video {Path} for pin {Id} is missing, entry pruned", r.SourcePath, r.Id);
                    return true;
                }
                return false;
            });
            if (removed > 0) FlushNoLock();
            DeleteOrphanFilesNoLock();
            return list.Select(r => r.Clone()).ToList();
        }
    }

    /// <summary>Write the PNG of an image pin through a temp file, then return its file name.
    /// <paramref name="writePng"/> streams the encoded image straight to disk, so no extra
    /// in-memory copy of the bitmap is made.</summary>
    public string WriteImage(Guid id, Action<Stream> writePng)
    {
        ArgumentNullException.ThrowIfNull(writePng);
        var name = ImageFileNameFor(id);
        lock (_gate)
        {
            Directory.CreateDirectory(FolderPath);
            var path = Path.Combine(FolderPath, name);
            var tmp = path + ".tmp";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    writePng(fs);
                }
                File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                TryDelete(tmp);
                throw;
            }
        }
        return name;
    }

    /// <summary>Add or replace the entry with the same id.</summary>
    public void Upsert(PinRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            var list = EnsureLoadedNoLock();
            var copy = record.Clone();
            var idx = list.FindIndex(r => r.Id == record.Id);
            if (idx >= 0) list[idx] = copy;
            else list.Add(copy);
            FlushNoLock();
        }
    }

    /// <summary>Drop the entry and its PNG. Unknown ids are a no-op.</summary>
    public void Remove(Guid id)
    {
        lock (_gate)
        {
            var list = EnsureLoadedNoLock();
            var removed = list.RemoveAll(r => r.Id == id);
            TryDelete(Path.Combine(FolderPath, ImageFileNameFor(id)));
            if (removed > 0) FlushNoLock();
        }
    }

    /// <summary>Forget every pin: manifest and PNGs.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _cache = new List<PinRecord>();
            if (!Directory.Exists(FolderPath)) return;
            TryDelete(ManifestPath);
            foreach (var file in Directory.EnumerateFiles(FolderPath, "*.png")) TryDelete(file);
            foreach (var file in Directory.EnumerateFiles(FolderPath, "*.tmp")) TryDelete(file);
        }
    }

    private List<PinRecord> EnsureLoadedNoLock()
    {
        if (_cache is not null) return _cache;
        _cache = new List<PinRecord>();
        if (!File.Exists(ManifestPath)) return _cache;
        try
        {
            var raw = File.ReadAllText(ManifestPath);
            var manifest = JsonSerializer.Deserialize<PinManifest>(raw, JsonOptions);
            if (manifest?.Pins is { } pins) _cache.AddRange(pins.Where(p => p is not null));
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Pins: {File} is unreadable, renamed aside and starting without pins", ManifestFileName);
            try { File.Move(ManifestPath, ManifestPath + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true); }
            catch (IOException) { /* best-effort */ }
        }
        return _cache;
    }

    private void FlushNoLock()
    {
        Directory.CreateDirectory(FolderPath);
        var json = JsonSerializer.Serialize(new PinManifest { Pins = _cache! }, JsonOptions);
        var tmp = ManifestPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, ManifestPath, overwrite: true);
    }

    private void DeleteOrphanFilesNoLock()
    {
        if (!Directory.Exists(FolderPath)) return;
        var referenced = new HashSet<string>(
            _cache!.Where(r => r.FileName is not null).Select(r => r.FileName!),
            StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(FolderPath, "*.png"))
        {
            if (referenced.Contains(Path.GetFileName(file))) continue;
            _logger.LogInformation("Pins: deleting orphan image {File}", Path.GetFileName(file));
            TryDelete(file);
        }
        foreach (var file in Directory.EnumerateFiles(FolderPath, "*.tmp")) TryDelete(file);
    }

    /// <summary>A hand-edited manifest must not point outside the Pins folder.</summary>
    private static bool IsPlainFileName(string name)
        => name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name != "." && name != "..";

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Pins: could not delete {Path}", path);
        }
    }
}
