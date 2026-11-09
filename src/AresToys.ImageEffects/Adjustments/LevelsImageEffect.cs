using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Adjustments;

/// <summary>Effect inspired by ShareX. Photoshop-style
/// levels: input black/white set the range that gets stretched, gamma curves the midtones,
/// output black/white compress the result back into a sub-range.</summary>
public sealed class LevelsImageEffect : AdjustmentImageEffectBase
{
    public override string Id => "levels";
    public override string Name => "Levels";

    [EffectParameter(0, 255, DisplayName = "Input black")]
    public int InputBlack { get; set; }

    [EffectParameter(0, 255, DisplayName = "Input white")]
    public int InputWhite { get; set; } = 255;

    [EffectParameter(0.1, 5, 0.1, DisplayName = "Gamma", Decimals = 2)]
    public float Gamma { get; set; } = 1f;

    [EffectParameter(0, 255, DisplayName = "Output black")]
    public int OutputBlack { get; set; }

    [EffectParameter(0, 255, DisplayName = "Output white")]
    public int OutputWhite { get; set; } = 255;

    public override SKBitmap Apply(SKBitmap source)
    {
        var inBlack = Math.Clamp(InputBlack, 0, 255);
        var inWhite = Math.Clamp(InputWhite, 0, 255);
        var outBlack = Math.Clamp(OutputBlack, 0, 255);
        var outWhite = Math.Clamp(OutputWhite, 0, 255);
        var gamma = Math.Clamp(Gamma, 0.1f, 5f);

        if (inWhite <= inBlack) inWhite = Math.Min(255, inBlack + 1);
        if (outWhite < outBlack) (outBlack, outWhite) = (outWhite, outBlack);

        if (inBlack == 0 && inWhite == 255 && Math.Abs(gamma - 1f) < 0.0001f
            && outBlack == 0 && outWhite == 255) return source.Copy();

        float inRange = inWhite - inBlack;
        float outRange = outWhite - outBlack;

        return ApplyPixelOperation(source, c => new SKColor(
            Map(c.Red, inBlack, inRange, gamma, outBlack, outRange),
            Map(c.Green, inBlack, inRange, gamma, outBlack, outRange),
            Map(c.Blue, inBlack, inRange, gamma, outBlack, outRange),
            c.Alpha));
    }

    private static byte Map(byte value, int inBlack, float inRange, float gamma, int outBlack, float outRange)
    {
        var normalized = Math.Clamp((value - inBlack) / inRange, 0f, 1f);
        var corrected = MathF.Pow(normalized, gamma);
        var output = outBlack + (corrected * outRange);
        if (output <= 0f) return 0;
        if (output >= 255f) return 255;
        return (byte)MathF.Round(output);
    }
}
