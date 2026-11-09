using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AresToys.ImageEffects.Drawing;
using SkiaSharp;

namespace AresToys.ImageEffects.Serialization;

/// <summary>JSON read/write for <see cref="EffectPreset"/>. Writes ShareX's standard format —
/// PascalCase property names, bare-class-name <c>$type</c> discriminator, <c>"X, Y"</c> Point
/// strings, <c>"L, T, R, B"</c> Padding strings, nested gradient objects — so a <c>.sxie</c>
/// exported by us opens in ShareX (and vice-versa) without translation. We're a fork; matching
/// the parent format end-to-end keeps the data layer interoperable.</summary>
public sealed class EffectPresetSerializer
{
    private readonly ImageEffectRegistry _registry;

    public EffectPresetSerializer(ImageEffectRegistry? registry = null)
    {
        _registry = registry ?? ImageEffectRegistry.Default;
    }

    // -------- Serialize (ShareX format) --------

    public string Serialize(EffectPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("Name", preset.Name ?? string.Empty);
            writer.WriteStartArray("Effects");
            foreach (var entry in preset.Effects)
            {
                if (entry.Effect is null) continue;
                WriteEffect(writer, entry);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteEffect(Utf8JsonWriter writer, EffectPresetEntry entry)
    {
        var effect = entry.Effect!;
        var type = effect.GetType();
        // ShareX's KnownTypesSerializationBinder writes Type.Name as the $type. Our class names
        // carry an "ImageEffect" suffix that ShareX's legacy classes don't, so strip it.
        var className = type.Name.EndsWith("ImageEffect", StringComparison.Ordinal)
            ? type.Name[..^"ImageEffect".Length]
            : type.Name;

        writer.WriteStartObject();
        writer.WriteString("$type", className);

        var props = PropertyCache.For(type);

        // Reassemble Point pairs: any `<base>X` + `<base>Y` int pair gets written as
        // `<base>: "X, Y"` to match ShareX's Point serialisation. DrawImage's Width/Height
        // becomes "Size" (the same shape ShareX uses for its DrawImageSizeMode).
        var skipNames = new HashSet<string>(StringComparer.Ordinal);
        var pairs = new Dictionary<string, (PropertyInfo X, PropertyInfo Y, string OutputName)>(StringComparer.Ordinal);
        foreach (var p in props)
        {
            if (p.PropertyType != typeof(int) || !p.Name.EndsWith('X')) continue;
            var baseName = p.Name[..^1];
            var pY = Array.Find(props, x => x.Name == baseName + "Y" && x.PropertyType == typeof(int));
            if (pY is null) continue;
            pairs[p.Name] = (p, pY, baseName);
            skipNames.Add(p.Name);
            skipNames.Add(pY.Name);
        }
        if (className == "DrawImage")
        {
            var w = Array.Find(props, x => x.Name == "Width" && x.PropertyType == typeof(int));
            var h = Array.Find(props, x => x.Name == "Height" && x.PropertyType == typeof(int));
            if (w is not null && h is not null)
            {
                pairs[w.Name] = (w, h, "Size");
                skipNames.Add(w.Name);
                skipNames.Add(h.Name);
            }
        }

        var emittedPair = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in props)
        {
            if (skipNames.Contains(p.Name))
            {
                if (pairs.TryGetValue(p.Name, out var pair) && emittedPair.Add(pair.OutputName))
                {
                    var x = (int)pair.X.GetValue(effect)!;
                    var y = (int)pair.Y.GetValue(effect)!;
                    writer.WriteString(pair.OutputName, $"{x}, {y}");
                }
                continue;
            }
            var value = p.GetValue(effect);
            writer.WritePropertyName(p.Name);
            WriteValue(writer, value, p.PropertyType);
        }

        // ShareX's serialized output writes Enabled last; matching the order keeps diffs against
        // gallery files small and aids readability when comparing two .sxie packages.
        writer.WriteBoolean("Enabled", entry.Enabled);
        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value, Type type)
    {
        if (value is null) { writer.WriteNullValue(); return; }

        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying == typeof(bool)) { writer.WriteBooleanValue((bool)value); return; }
        if (underlying == typeof(int)) { writer.WriteNumberValue((int)value); return; }
        if (underlying == typeof(float)) { writer.WriteNumberValue((float)value); return; }
        if (underlying == typeof(double)) { writer.WriteNumberValue((double)value); return; }
        if (underlying == typeof(long)) { writer.WriteNumberValue((long)value); return; }
        if (underlying == typeof(string)) { writer.WriteStringValue((string)value); return; }
        if (underlying == typeof(SKColor)) { writer.WriteStringValue(FormatColor((SKColor)value)); return; }
        if (underlying == typeof(Padding))
        {
            var p = (Padding)value;
            writer.WriteStringValue($"{p.Left}, {p.Top}, {p.Right}, {p.Bottom}");
            return;
        }
        if (underlying == typeof(GradientInfo))
        {
            WriteGradient(writer, (GradientInfo)value);
            return;
        }
        if (underlying.IsEnum)
        {
            writer.WriteStringValue(value.ToString());
            return;
        }
        // Fallback: let STJ handle anything we don't special-case (lists, nested records).
        JsonSerializer.Serialize(writer, value, type);
    }

    private static void WriteGradient(Utf8JsonWriter writer, GradientInfo info)
    {
        writer.WriteStartObject();
        writer.WriteString("Type", info.Type.ToString());
        writer.WriteStartArray("Colors");
        foreach (var stop in info.Colors)
        {
            writer.WriteStartObject();
            writer.WriteString("Color", FormatColor(stop.Color));
            writer.WriteNumber("Location", stop.Location);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static string FormatColor(SKColor c) =>
        c.Alpha == 255 ? $"{c.Red}, {c.Green}, {c.Blue}"
                       : $"{c.Alpha}, {c.Red}, {c.Green}, {c.Blue}";

    // -------- Deserialize --------

    public EffectPreset? Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        return SxiePresetImporter.Import(json, _registry);
    }

    /// <summary>Effect-property reflection cache. Built once per CLR type: a fresh
    /// <c>Type.GetProperties()</c> call costs ~10× more than a dict lookup and we hit this
    /// path for every effect on every save/load.</summary>
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
