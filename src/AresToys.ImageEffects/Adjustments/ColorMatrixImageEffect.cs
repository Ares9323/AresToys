using SkiaSharp;

namespace AresToys.ImageEffects.Adjustments;

/// <summary>Effect inspired by ShareX. 4×5 colour
/// matrix; identity on the diagonal by default. The 20 properties round-trip cleanly with
/// .sxie presets that wire e.g. <c>"Rg": 0.2</c>.</summary>
public sealed class ColorMatrixImageEffect : AdjustmentImageEffectBase
{
    public override string Id => "color_matrix";
    public override string Name => "Color matrix";

    public float Rr { get; set; } = 1f; public float Rg { get; set; } public float Rb { get; set; } public float Ra { get; set; } public float Ro { get; set; }
    public float Gr { get; set; } public float Gg { get; set; } = 1f; public float Gb { get; set; } public float Ga { get; set; } public float Go { get; set; }
    public float Br { get; set; } public float Bg { get; set; } public float Bb { get; set; } = 1f; public float Ba { get; set; } public float Bo { get; set; }
    public float Ar { get; set; } public float Ag { get; set; } public float Ab { get; set; } public float Aa { get; set; } = 1f; public float Ao { get; set; }

    public override SKBitmap Apply(SKBitmap source)
    {
        // Skia uses row-major 4×5: [R'/G'/B'/A'] = M·[R/G/B/A/1]. Translate is /255 because
        // Skia matrices operate on 0..1 channels; user enters 0..255 offset.
        float[] matrix =
        {
            Rr, Rg, Rb, Ra, Ro / 255f,
            Gr, Gg, Gb, Ga, Go / 255f,
            Br, Bg, Bb, Ba, Bo / 255f,
            Ar, Ag, Ab, Aa, Ao / 255f,
        };
        return ApplyColorMatrix(source, matrix);
    }
}
