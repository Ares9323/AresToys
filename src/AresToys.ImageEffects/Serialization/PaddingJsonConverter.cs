using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AresToys.ImageEffects.Drawing;

namespace AresToys.ImageEffects.Serialization;

/// <summary>Padding round-trips as a comma-separated string <c>"L, T, R, B"</c> — the same
/// shape ShareX writes via <c>System.Windows.Forms.Padding</c>'s default TypeConverter. We
/// also accept a single int (sets all four sides) for hand-authored configs.</summary>
public sealed class PaddingJsonConverter : JsonConverter<Padding>
{
    public override Padding Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            var all = reader.GetInt32();
            return new Padding(all, all, all, all);
        }
        if (reader.TokenType != JsonTokenType.String) return Padding.Empty;
        var raw = reader.GetString();
        if (string.IsNullOrWhiteSpace(raw)) return Padding.Empty;
        var parts = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var single))
            return new Padding(single, single, single, single);
        if (parts.Length == 4
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var t)
            && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r)
            && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
            return new Padding(l, t, r, b);
        return Padding.Empty;
    }

    public override void Write(Utf8JsonWriter writer, Padding value, JsonSerializerOptions options)
    {
        writer.WriteStringValue($"{value.Left}, {value.Top}, {value.Right}, {value.Bottom}");
    }
}
