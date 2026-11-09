using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkiaSharp;

namespace AresToys.ImageEffects.Serialization;

/// <summary>JSON converter for <see cref="SKColor"/> matching ShareX's
/// <c>System.Drawing.Color</c> serialisation conventions:
/// <list type="bullet">
///   <item>Named colours: <c>"Black"</c>, <c>"Transparent"</c>, <c>"White"</c>, … (any
///   public static <see cref="SKColors"/> field).</item>
///   <item>RGB: <c>"R, G, B"</c> — three comma-separated 0..255 ints.</item>
///   <item>ARGB: <c>"A, R, G, B"</c> — four comma-separated 0..255 ints, alpha FIRST. This
///   matches <c>Color.FromArgb(int alpha, int r, int g, int b)</c> and is what ShareX writes
///   via <c>System.Drawing.Color</c>'s default <c>TypeConverter</c>. Earlier we treated this
///   as RGBA which made translucent borders import as cyan-ish opaque colours (e.g. the
///   macOSBigSur preset's <c>"40, 255, 255, 255"</c> highlight).</item>
/// </list>
/// Output mirrors the input format: RGB when fully opaque, ARGB otherwise.</summary>
public sealed class SkColorJsonConverter : JsonConverter<SKColor>
{
    public override SKColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Expected colour string.");
        var raw = reader.GetString();
        return Parse(raw);
    }

    public override void Write(Utf8JsonWriter writer, SKColor value, JsonSerializerOptions options)
    {
        // ShareX uses ARGB ordering for 4-component colours (alpha first), matching
        // System.Drawing.Color.FromArgb. Opaque colours stay 3-component RGB.
        writer.WriteStringValue(value.Alpha == 255
            ? $"{value.Red}, {value.Green}, {value.Blue}"
            : $"{value.Alpha}, {value.Red}, {value.Green}, {value.Blue}");
    }

    public static SKColor Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return SKColors.Transparent;
        var trimmed = raw.Trim();

        // Comma-separated form. ShareX writes ints in invariant culture so we don't worry
        // about locale-flipped decimal commas. Three components = RGB (opaque); four
        // components = ARGB (alpha first), NOT RGBA — this is what System.Drawing.Color
        // emits via TypeConverter. Treating it as RGBA caused translucent borders to come
        // through with the alpha value sitting in the red channel.
        if (trimmed.Contains(','))
        {
            var parts = trimmed.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3
                && TryByte(parts[0], out var r3) && TryByte(parts[1], out var g3) && TryByte(parts[2], out var b3))
            {
                return new SKColor(r3, g3, b3, 255);
            }
            if (parts.Length == 4
                && TryByte(parts[0], out var a4) && TryByte(parts[1], out var r4)
                && TryByte(parts[2], out var g4) && TryByte(parts[3], out var b4))
            {
                return new SKColor(r4, g4, b4, a4);
            }
        }

        // #RRGGBB / #RRGGBBAA hex form (less common from ShareX but handy when authoring by hand).
        if (trimmed.StartsWith('#') && (trimmed.Length == 7 || trimmed.Length == 9))
        {
            try
            {
                var r = byte.Parse(trimmed.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var g = byte.Parse(trimmed.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var b = byte.Parse(trimmed.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var a = (byte)255;
                if (trimmed.Length == 9)
                    a = byte.Parse(trimmed.AsSpan(7, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return new SKColor(r, g, b, a);
            }
            catch (FormatException) { /* fall through to named-colour lookup */ }
        }

        // Named colour: SKColors has every CSS / .NET name as a public static field.
        var field = typeof(SKColors).GetField(trimmed, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
        if (field?.GetValue(null) is SKColor named) return named;

        // Unknown payload — fall back to opaque black so the rest of the preset still loads.
        return SKColors.Black;
    }

    private static bool TryByte(string s, out byte value)
    {
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i is >= 0 and <= 255)
        {
            value = (byte)i;
            return true;
        }
        value = 0;
        return false;
    }
}
