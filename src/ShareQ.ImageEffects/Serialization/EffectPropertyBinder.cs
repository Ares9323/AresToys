using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShareQ.ImageEffects.Serialization;

/// <summary>Reflective copy of JSON object properties into a live <see cref="ImageEffect"/>.
/// Both our own preset reader and the ShareX <c>.sxie</c> importer go through this — they
/// just feed it different <see cref="JsonElement"/> shapes (camelCase vs PascalCase). A
/// missing property is fine (effect keeps its default); an incompatible value is logged-and-
/// skipped rather than aborting the load, so one bad slider doesn't poison a whole preset.</summary>
internal static class EffectPropertyBinder
{
    public static void Apply(ImageEffect effect, JsonElement source, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(effect);
        if (source.ValueKind != JsonValueKind.Object) return;
        options ??= _defaults;

        foreach (var prop in EffectPresetSerializer.PropertyCache.For(effect.GetType()))
        {
            var attr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
            var camel = attr?.Name ?? options.PropertyNamingPolicy?.ConvertName(prop.Name) ?? prop.Name;

            // Try camelCase first (our format), then raw PropertyName (ShareX format).
            if (!source.TryGetProperty(camel, out var value)
                && !source.TryGetProperty(prop.Name, out value)) continue;

            try
            {
                var typed = value.Deserialize(prop.PropertyType, options);
                prop.SetValue(effect, typed);
            }
            catch (JsonException)
            {
                // Tolerate per-property mismatches — a typo'd numeric, a renamed enum value,
                // a removed parameter from an older ShareX build. Rest of the chain still loads.
            }
        }
    }

    private static readonly JsonSerializerOptions _defaults = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}
