using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using AresToys.AI;
using AresToys.Storage.Settings;

namespace AresToys.App.Services;

/// <summary>JSON converter for <see cref="System.Drawing.Color"/>. The default serializer
/// can't round-trip Color: it's a struct with no parameterless ctor (factory only via
/// <c>FromArgb</c>), so the deserializer ends up with <c>default(Color)</c> = empty/black.
/// We serialize as the packed ARGB int (matches <c>Color.ToArgb()</c>) which round-trips
/// losslessly. Without this, every saved preset's IgnoreColor would silently come back
/// as transparent black at load time.</summary>
internal sealed class ColorJsonConverter : JsonConverter<System.Drawing.Color>
{
    public override System.Drawing.Color Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number) return System.Drawing.Color.FromArgb(reader.GetInt32());
        return default;
    }

    public override void Write(Utf8JsonWriter writer, System.Drawing.Color value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value.ToArgb());
    }
}

/// <summary>Persist user-saved trace presets in the settings DB under a single JSON list
/// keyed <c>trace.custom_presets</c>. Stock presets stay in code (see
/// <see cref="TracePresets.Stock"/>); the store only holds what the user explicitly
/// saves via "Save preset…" in the trace window. Save replaces by name (lower-case) so
/// the user can iterate on a preset and overwrite it without proliferating duplicates.</summary>
public sealed class TracePresetStore
{
    private const string Key = "trace.custom_presets";
    private const string LastUsedKey = "trace.last_preset";
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Converters = { new ColorJsonConverter() }
    };
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
            var list = JsonSerializer.Deserialize<List<TracePreset>>(raw, JsonOpts);
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
        var json = JsonSerializer.Serialize(existing, JsonOpts);
        await _settings.SetAsync(Key, json, sensitive: false, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string name, CancellationToken ct)
    {
        var existing = (await GetAllAsync(ct).ConfigureAwait(false)).ToList();
        existing.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        var json = JsonSerializer.Serialize(existing, JsonOpts);
        await _settings.SetAsync(Key, json, sensitive: false, ct).ConfigureAwait(false);
    }

    /// <summary>Persist the name of the preset the user last picked. Restored on next
    /// TraceWindow open so the user comes back to the parameters they were dialling in.</summary>
    public Task SetLastUsedAsync(string presetName, CancellationToken ct)
        => _settings.SetAsync(LastUsedKey, presetName, sensitive: false, ct);

    public Task<string?> GetLastUsedAsync(CancellationToken ct)
        => _settings.GetAsync(LastUsedKey, ct);
}
