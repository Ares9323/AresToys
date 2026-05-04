using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShareQ.ImageEffects.Serialization;

/// <summary>JSON read/write for <see cref="EffectPreset"/>. Effects are polymorphic — each
/// entry stores its <c>id</c> string (matching <see cref="ImageEffect.Id"/>) plus whatever
/// public properties the concrete effect class exposes. The <c>id</c> is the only
/// discriminator we need: the registry resolves it back to a CLR type at deserialize-time, so
/// adding a new effect doesn't require touching this file. Properties are reflected; anything
/// public/settable round-trips, anything `[JsonIgnore]`-tagged stays out.
///
/// Format example:
/// <code>
/// {
///   "id": "...", "name": "Polaroid",
///   "effects": [
///     { "id": "saturation", "enabled": true, "amount": -20 },
///     { "id": "vignette",  "enabled": true, "intensity": 50 }
///   ]
/// }
/// </code></summary>
public sealed class EffectPresetSerializer
{
    private readonly ImageEffectRegistry _registry;
    private readonly JsonSerializerOptions _options;

    public EffectPresetSerializer(ImageEffectRegistry? registry = null)
    {
        _registry = registry ?? ImageEffectRegistry.Default;
        _options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new EffectPresetConverter(_registry) },
        };
    }

    public string Serialize(EffectPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return JsonSerializer.Serialize(preset, _options);
    }

    public EffectPreset? Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        return JsonSerializer.Deserialize<EffectPreset>(json, _options);
    }

    /// <summary>Effect-property reflection cache. Built once per (registry, type) pair: a fresh
    /// `Type.GetProperties()` call costs ~10× more than a dict lookup and we hit this path
    /// for every effect on every save/load.</summary>
    internal static class PropertyCache
    {
        private static readonly Dictionary<Type, PropertyInfo[]> _cache = new();
        private static readonly Lock _gate = new();

        public static PropertyInfo[] For(Type type)
        {
            lock (_gate)
            {
                if (_cache.TryGetValue(type, out var cached)) return cached;
                var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanRead && p.CanWrite && p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
                    .ToArray();
                _cache[type] = props;
                return props;
            }
        }
    }
}

/// <summary>Reads/writes the entire preset tree. We don't use STJ's [JsonPolymorphic] because
/// it requires declaring every derived type at the base class — adding a new effect would
/// mean editing <see cref="ImageEffect"/> too. Reflecting via the registry keeps effects as
/// drop-in additions.</summary>
internal sealed class EffectPresetConverter : JsonConverter<EffectPreset>
{
    private readonly ImageEffectRegistry _registry;

    public EffectPresetConverter(ImageEffectRegistry registry) { _registry = registry; }

    public override EffectPreset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException();
        var preset = new EffectPreset();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return preset;
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
            var prop = reader.GetString();
            reader.Read();
            switch (prop?.ToLowerInvariant())
            {
                case "id": preset.Id = reader.GetString() ?? Guid.NewGuid().ToString("N"); break;
                case "name": preset.Name = reader.GetString() ?? string.Empty; break;
                case "effects": preset.Effects = ReadEffects(ref reader, options); break;
                default: reader.Skip(); break;
            }
        }
        throw new JsonException("Unexpected end of JSON.");
    }

    private List<EffectPresetEntry> ReadEffects(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException();
        var result = new List<EffectPresetEntry>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) return result;
            if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException();

            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrEmpty(id)) continue; // malformed entry, skip rather than throw

            var effect = _registry.Create(id);
            if (effect is null) continue; // unknown id (preset from a future version) — skip

            var enabled = !root.TryGetProperty("enabled", out var enEl) || enEl.GetBoolean();
            EffectPropertyBinder.Apply(effect, root, options);
            result.Add(new EffectPresetEntry(effect, enabled));
        }
        throw new JsonException("Unexpected end of effects array.");
    }

    public override void Write(Utf8JsonWriter writer, EffectPreset preset, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", preset.Id);
        writer.WriteString("name", preset.Name);
        writer.WriteStartArray("effects");
        foreach (var entry in preset.Effects)
        {
            if (entry.Effect is null) continue;
            writer.WriteStartObject();
            writer.WriteString("id", entry.Effect.Id);
            writer.WriteBoolean("enabled", entry.Enabled);
            var props = EffectPresetSerializer.PropertyCache.For(entry.Effect.GetType());
            foreach (var prop in props)
            {
                var attr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
                var key = attr?.Name ?? options.PropertyNamingPolicy?.ConvertName(prop.Name) ?? prop.Name;
                var value = prop.GetValue(entry.Effect);
                writer.WritePropertyName(key);
                JsonSerializer.Serialize(writer, value, prop.PropertyType, options);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
