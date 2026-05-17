namespace AresToys.App.Services;

/// <summary>Maps <see cref="PipelineTasks.ConvertColorTask"/>'s compact format codes
/// (<c>"hex-hash-alpha"</c>) to user-facing dropdown labels that include a syntax preview
/// (<c>"Hex with # and alpha (#RRGGBBAA)"</c>) so the user picks by example instead of guessing
/// what each code means. Raw values stay compact for stable JSON serialization; the display
/// labels are computed per render and never persisted.</summary>
internal static class ColorFormatLabels
{
    // Pipe separator instead of "Name (preview)" so the formats whose preview *itself* contains
    // parens (rgb(…), rgba(…), hsb(…), cmyk(…)) don't end up with doubled parens like
    // "CMYK (cmyk(C%, M%, Y%, K%))". For formats with no natural preview punctuation (Hex
    // variants, Decimal, Linear, BGRA) the pipe + bare preview reads the same.
    public static string LabelFor(string raw) => raw switch
    {
        "hex"            => "HEX | RRGGBB",
        "hex-hash"       => "#HEX | #RRGGBB",
        "hex-alpha"      => "HEX with alpha | RRGGBBAA",
        "hex-hash-alpha" => "#HEX with alpha | #RRGGBBAA",
        "rgb"            => "RGB | rgb(R, G, B)",
        "rgba"           => "RGBA | rgba(R, G, B, A)",
        "hsb"            => "HSB | hsb(H°, S%, B%)",
        "cmyk"           => "CMYK | cmyk(C%, M%, Y%, K%)",
        "decimal"        => "Decimal ARGB | AARRGGBB",
        "linear"         => "UE FLinearColor | (R=1.0,G=1.0,B=1.0,A=1.0)",
        "bgra"           => "UE FColor | (B=255,G=255,R=255,A=255) ",
        _                => raw,
    };
}
