using System.Text.Json;
using Microsoft.Extensions.Logging;
using ShareQ.AI;
using ShareQ.Storage.Settings;

namespace ShareQ.App.Services;

/// <summary>Persist user-saved trace presets in the settings DB under a single JSON list
/// keyed <c>trace.custom_presets</c>. Stock presets stay in code (see
/// <see cref="TracePresets.Stock"/>); the store only holds what the user explicitly
/// saves via "Save preset…" in the trace window. Save replaces by name (lower-case) so
/// the user can iterate on a preset and overwrite it without proliferating duplicates.</summary>
public sealed class TracePresetStore
{
    private const string Key = "trace.custom_presets";
    private readonly ISettingsStore _settings;
    private readonly ILogger<TracePresetStore> _logger;

    public TracePresetStore(ISettingsStore settings, ILogger<TracePresetStore> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TracePreset>> GetAllAsync(CancellationToken ct)
    {
        var raw = await _settings.GetAsync(Key, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(raw)) return Array.Empty<TracePreset>();
        try
        {
            var list = JsonSerializer.Deserialize<List<TracePreset>>(raw);
            return list ?? new List<TracePreset>();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "TracePresetStore: malformed JSON in {Key}; ignoring", Key);
            return Array.Empty<TracePreset>();
        }
    }

    public async Task SaveAsync(TracePreset preset, CancellationToken ct)
    {
        var existing = (await GetAllAsync(ct).ConfigureAwait(false)).ToList();
        // Replace-by-name so "Save" on an existing preset name overwrites instead of duplicating.
        existing.RemoveAll(p => string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
        existing.Add(preset);
        var json = JsonSerializer.Serialize(existing);
        await _settings.SetAsync(Key, json, sensitive: false, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string name, CancellationToken ct)
    {
        var existing = (await GetAllAsync(ct).ConfigureAwait(false)).ToList();
        existing.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        var json = JsonSerializer.Serialize(existing);
        await _settings.SetAsync(Key, json, sensitive: false, ct).ConfigureAwait(false);
    }
}
