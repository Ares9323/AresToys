using System.Text.Json;
using AresToys.Editor.Model;
using AresToys.Storage.Settings;

namespace AresToys.Editor.Persistence;

/// <summary>Persisted "Recent colors" ring shown in every <see cref="Views.ColorPickerWindow"/>.
/// Keeps an in-memory mirror of the persisted list so a push is visible to the UI on the same
/// tick it happens (the settings round-trip is async and the picker can't await it), and raises
/// <see cref="Changed"/> so an already-open picker refreshes its palette instead of showing the
/// snapshot it loaded when it opened.</summary>
public sealed class ColorRecentsStore
{
    public const int MaxEntries = 8;
    private const string SettingsKey = "editor.color.recents";

    private readonly ISettingsStore _settings;
    /// <summary>Serialises the read-modify-write in <see cref="PushAsync"/> by chaining each push
    /// onto the previous one. Pushes arrive from several surfaces (editor swatches, wheel
    /// launcher, screen sampler, Theme tab) and more than one can be in flight at once; without
    /// the chain two concurrent pushes both read the pre-push list and the second write drops the
    /// first colour. A task chain rather than a SemaphoreSlim so the store stays non-disposable —
    /// it's a DI singleton that lives for the whole process.</summary>
    private readonly object _sync = new();
    private Task _tail = Task.CompletedTask;
    private IReadOnlyList<ShapeColor> _cache = [];
    private bool _loaded;

    public ColorRecentsStore(ISettingsStore settings)
    {
        _settings = settings;
    }

    /// <summary>Latest known list, without touching the settings store. Empty until the first
    /// <see cref="LoadAsync"/>; kept in sync by every <see cref="PushAsync"/>.</summary>
    public IReadOnlyList<ShapeColor> Current => _cache;

    /// <summary>Raised (on the calling thread) whenever the list changes. Hosts forward this to
    /// <c>ColorSwatchButton.CurrentRecents</c> so every picker — including one that's already
    /// open — sees the new colour.</summary>
    public event EventHandler<IReadOnlyList<ShapeColor>>? Changed;

    /// <summary>Hydrate from the settings store the first time; after that hand back the cache.
    /// Callers re-load on every picker open, and re-reading would race a push whose write hasn't
    /// landed yet — the cache would be rolled back to the pre-push list and the colour would
    /// vanish from the palette again.</summary>
    public async Task<IReadOnlyList<ShapeColor>> LoadAsync(CancellationToken cancellationToken)
    {
        if (_loaded) return _cache;
        var raw = await _settings.GetAsync(SettingsKey, cancellationToken).ConfigureAwait(false);
        _cache = Parse(raw);
        _loaded = true;
        return _cache;
    }

    public Task PushAsync(ShapeColor color, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            var next = PushAfterAsync(_tail, color, cancellationToken);
            _tail = next;
            return next;
        }
    }

    private async Task PushAfterAsync(Task previous, ShapeColor color, CancellationToken cancellationToken)
    {
        // A previous push failing (or being cancelled) must not poison the queue — its caller
        // already observed the fault through the task it was handed.
        try { await previous.ConfigureAwait(false); }
        catch { /* intentionally ignored: see above */ }

        // Read through the store only once per process: after that the in-memory cache is
        // authoritative (we're the only writer of this key) and skipping the round-trip is what
        // lets a push land in the UI immediately.
        if (!_loaded)
        {
            var raw = await _settings.GetAsync(SettingsKey, cancellationToken).ConfigureAwait(false);
            _cache = Parse(raw);
            _loaded = true;
        }

        var current = _cache.ToList();
        current.RemoveAll(c => c == color);
        current.Insert(0, color);
        if (current.Count > MaxEntries) current = current.Take(MaxEntries).ToList();
        _cache = current;

        var dtos = current.Select(c => new Dto(c.A, c.R, c.G, c.B)).ToList();
        var json = JsonSerializer.Serialize(dtos);
        await _settings.SetAsync(SettingsKey, json, sensitive: false, cancellationToken).ConfigureAwait(false);

        Changed?.Invoke(this, current);
    }

    private static IReadOnlyList<ShapeColor> Parse(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return [];
        try
        {
            var entries = JsonSerializer.Deserialize<List<Dto>>(raw);
            if (entries is null) return [];
            return entries.Select(d => new ShapeColor(d.A, d.R, d.G, d.B)).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed record Dto(byte A, byte R, byte G, byte B);
}
