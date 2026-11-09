using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Adjustments;

/// <summary>Effect inspired by ShareX. Builds a
/// per-channel histogram, drops <see cref="ClipPercent"/>% of pixels from each end (so a few
/// bright/dark outliers don't lock the levels), then linearly stretches the rest to 0..255.
/// Two passes over the buffer (histogram + remap), still cheap at preview sizes.</summary>
public sealed class AutoContrastImageEffect : AdjustmentImageEffectBase
{
    public override string Id => "auto_contrast";
    public override string Name => "Auto contrast";

    [EffectParameter(0, 20, 0.1, DisplayName = "Clip %", Decimals = 2)]
    public float ClipPercent { get; set; } = 0.5f;

    public override SKBitmap Apply(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var clipPercent = Math.Clamp(ClipPercent, 0f, 20f);
        var srcPixels = source.Pixels;
        var total = srcPixels.Length;
        if (total == 0) return source.Copy();

        var clipCount = (int)MathF.Round(total * (clipPercent / 100f));

        int[] histR = new int[256], histG = new int[256], histB = new int[256];
        for (var i = 0; i < total; i++)
        {
            var c = srcPixels[i];
            histR[c.Red]++; histG[c.Green]++; histB[c.Blue]++;
        }

        FindRange(histR, clipCount, out var minR, out var maxR);
        FindRange(histG, clipCount, out var minG, out var maxG);
        FindRange(histB, clipCount, out var minB, out var maxB);

        if (maxR <= minR && maxG <= minG && maxB <= minB) return source.Copy();

        var dst = new SKColor[total];
        for (var i = 0; i < total; i++)
        {
            var c = srcPixels[i];
            dst[i] = new SKColor(
                Stretch(c.Red, minR, maxR),
                Stretch(c.Green, minG, maxG),
                Stretch(c.Blue, minB, maxB),
                c.Alpha);
        }

        return new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType) { Pixels = dst };
    }

    private static void FindRange(int[] histogram, int clipCount, out int min, out int max)
    {
        min = 0;
        var sum = 0;
        for (var i = 0; i < 256; i++)
        {
            sum += histogram[i];
            if (sum > clipCount) { min = i; break; }
        }
        max = 255;
        sum = 0;
        for (var i = 255; i >= 0; i--)
        {
            sum += histogram[i];
            if (sum > clipCount) { max = i; break; }
        }
    }

    private static byte Stretch(byte value, int min, int max)
    {
        if (max <= min) return value;
        if (value <= min) return 0;
        if (value >= max) return 255;
        return (byte)MathF.Round((value - min) * 255f / (max - min));
    }
}
