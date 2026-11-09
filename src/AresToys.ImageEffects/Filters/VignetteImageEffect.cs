using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Filters;

/// <summary>Vignette — darkens the corners with a radial gradient centred on the image.
/// Positive amount = darken corners; negative = lighten (uncommon but symmetric).</summary>
public sealed class VignetteImageEffect : FilterImageEffectBase
{
    public override string Id => "vignette";
    public override string Name => "Vignette";

    [EffectParameter(0, 100, DisplayName = "Amount")]
    public float Amount { get; set; } = 60f;

    [EffectParameter(0, 100, DisplayName = "Falloff")]
    public float Falloff { get; set; } = 50f;

    public override SKBitmap Apply(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var amount = Math.Clamp(Amount, 0f, 100f) / 100f;
        if (amount <= 0) return source.Copy();
        var falloff = Math.Clamp(Falloff, 0f, 100f) / 100f;

        var result = source.Copy();
        using var canvas = new SKCanvas(result);
        var cx = source.Width / 2f;
        var cy = source.Height / 2f;
        var maxR = MathF.Sqrt((cx * cx) + (cy * cy));

        // Radial gradient from transparent at the centre to black-amount at the edge.
        // Falloff offsets the inner stop so the dark only kicks in past a certain radius.
        using var shader = SKShader.CreateRadialGradient(
            new SKPoint(cx, cy), maxR,
            new[] { SKColors.Transparent, new SKColor(0, 0, 0, (byte)(255 * amount)) },
            new[] { Math.Clamp(falloff, 0f, 0.95f), 1f },
            SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawRect(0, 0, source.Width, source.Height, paint);
        return result;
    }
}
