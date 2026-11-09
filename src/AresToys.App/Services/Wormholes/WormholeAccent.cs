using System.Globalization;
using System.Windows.Media;

namespace AresToys.App.Services.Wormholes;

/// <summary>Reads and writes <see cref="WormholeRecord.AccentOverride"/>, the per-wormhole colour
/// that replaces the theme's accent on that window's ring. Stored as text because the record is
/// JSON on disk and a hex string survives hand-editing; parsing is deliberately forgiving on input
/// (with or without alpha, any casing, stray whitespace) and strict about everything else, so a
/// value that can't be understood falls back to the theme instead of throwing while painting.</summary>
public static class WormholeAccent
{
    /// <summary>How far the body backdrop is darkened from the chosen accent. The tiles and their
    /// labels sit on this surface, so it has to stay a background: at full saturation the icons
    /// fight it. 0.72 keeps the colour clearly readable while leaving the foreground legible.</summary>
    public const double BodyShade = 0.72;

    /// <summary>The header is darkened less than the body, keeping the strip distinguishable from
    /// the area below it the way the two themed Surface brushes already differ.</summary>
    public const double HeaderShade = 0.55;

    /// <summary>The outer ring takes the header's shade rather than the raw accent: at full
    /// saturation the ring outshone the window it frames. Defined in terms of
    /// <see cref="HeaderShade"/> so the two can't drift apart if that value is retuned.</summary>
    public const double RingShade = HeaderShade;

    /// <summary>Darken <paramref name="colour"/> towards black by <paramref name="amount"/>
    /// (0 = unchanged, 1 = black), leaving alpha alone.</summary>
    public static Color Shade(Color colour, double amount)
    {
        var keep = 1.0 - Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            colour.A,
            (byte)Math.Round(colour.R * keep),
            (byte)Math.Round(colour.G * keep),
            (byte)Math.Round(colour.B * keep));
    }

    /// <summary>Canonical stored form: <c>#AARRGGBB</c>, upper case. Keeping one form means saving
    /// the same colour twice doesn't rewrite the record with a different spelling.</summary>
    public static string Format(Color colour) =>
        string.Create(CultureInfo.InvariantCulture,
            $"#{colour.A:X2}{colour.R:X2}{colour.G:X2}{colour.B:X2}");

    /// <summary>The override colour, or null when there isn't a usable one — which is also what an
    /// empty field means: "use the theme".</summary>
    public static Color? TryParse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        var s = stored.Trim();
        if (s.Length is not (7 or 9) || s[0] != '#') return null;

        var hex = s[1..];
        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) return null;

        // 6 digits carry no alpha; a colour the user picked is meant to be visible, so default to
        // fully opaque rather than to zero (which would render as nothing at all).
        return hex.Length == 6
            ? Color.FromArgb(0xFF, (byte)(value >> 16), (byte)(value >> 8), (byte)value)
            : Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }
}
