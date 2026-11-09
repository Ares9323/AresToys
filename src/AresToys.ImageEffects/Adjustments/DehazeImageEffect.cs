using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Adjustments;

/// <summary>Effect inspired by ShareX. Dark-channel
/// prior dehazing: estimate atmospheric light from the brightest 0.1% of pixels, then
/// recover the scene by inverting the haze model <c>I = J·t + A·(1-t)</c>. Negative amount
/// runs the model in reverse to *add* haze.</summary>
public sealed class DehazeImageEffect : AdjustmentImageEffectBase
{
    public override string Id => "dehaze";
    public override string Name => "Dehaze";

    [EffectParameter(-100, 100, DisplayName = "Amount")]
    public float Amount { get; set; } = 50f;

    public override SKBitmap Apply(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var amount = Math.Clamp(Amount, -100f, 100f) / 100f;
        if (Math.Abs(amount) < 0.001f) return source.Copy();

        var pixels = source.Pixels;
        var count = pixels.Length;
        if (count == 0) return source.Copy();

        // Atmospheric light estimate: average of the top 0.1% brightest pixels (RGB mean).
        var topN = Math.Max(1, count / 1000);
        var brightness = new float[count];
        for (var i = 0; i < count; i++)
        {
            var c = pixels[i];
            brightness[i] = (c.Red + c.Green + c.Blue) / 3f;
        }
        var sorted = (float[])brightness.Clone();
        Array.Sort(sorted);
        var threshold = sorted[Math.Max(0, count - topN)];

        float sumAtmR = 0, sumAtmG = 0, sumAtmB = 0;
        var atmCount = 0;
        for (var i = 0; i < count; i++)
        {
            if (brightness[i] < threshold) continue;
            sumAtmR += pixels[i].Red;
            sumAtmG += pixels[i].Green;
            sumAtmB += pixels[i].Blue;
            atmCount++;
        }
        var atmR = atmCount > 0 ? sumAtmR / atmCount : 200f;
        var atmG = atmCount > 0 ? sumAtmG / atmCount : 200f;
        var atmB = atmCount > 0 ? sumAtmB / atmCount : 200f;

        var result = new SKColor[count];
        for (var i = 0; i < count; i++)
        {
            var c = pixels[i];
            float r = c.Red, g = c.Green, b = c.Blue;
            if (amount > 0f)
            {
                var darkChannel = MathF.Min(r / atmR, MathF.Min(g / atmG, b / atmB));
                var transmission = MathF.Max(1f - (amount * darkChannel), 0.1f);
                r = (r - (atmR * (1f - transmission))) / transmission;
                g = (g - (atmG * (1f - transmission))) / transmission;
                b = (b - (atmB * (1f - transmission))) / transmission;
            }
            else
            {
                var hazeAmount = -amount;
                r += (atmR - r) * hazeAmount;
                g += (atmG - g) * hazeAmount;
                b += (atmB - b) * hazeAmount;
            }
            result[i] = new SKColor(ClampToByte(r), ClampToByte(g), ClampToByte(b), c.Alpha);
        }

        return new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType) { Pixels = result };
    }

    private static byte ClampToByte(float value)
    {
        if (value <= 0f) return 0;
        if (value >= 255f) return 255;
        return (byte)MathF.Round(value);
    }
}
