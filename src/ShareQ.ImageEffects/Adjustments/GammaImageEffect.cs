using ShareQ.ImageEffects.Parameters;
using SkiaSharp;

namespace ShareQ.ImageEffects.Adjustments;

/// <summary>Ported from ShareX (GPL v3) — Adjustments/GammaImageEffect.cs. Gamma table is
/// rebuilt on demand and cached across calls so a slider sweep doesn't recompute 256 pow()
/// per frame.</summary>
public sealed class GammaImageEffect : AdjustmentImageEffectBase
{
    public override string Id => "gamma";
    public override string Name => "Gamma";

    [EffectParameter(0.1, 5, 0.1, DisplayName = "Amount", Decimals = 2)]
    public float Amount { get; set; } = 1f;

    private float _cachedAmount = float.NaN;
    private byte[]? _cachedTable;

    public override SKBitmap Apply(SKBitmap source)
    {
        // Guard against amount=0 producing 1/0 = ∞ in the pow exponent. The slider min is
        // 0.1 but the manual-entry TextBox can in principle land on 0 if the user types it.
        var amount = Amount < 0.01f ? 0.01f : Amount;

        if (_cachedTable is null || _cachedAmount != amount)
        {
            _cachedAmount = amount;
            _cachedTable = new byte[256];
            var inv = 1.0 / amount;
            for (var i = 0; i < 256; i++)
            {
                var val = i / 255.0;
                var corrected = Math.Pow(val, inv);
                _cachedTable[i] = (byte)(Math.Clamp(corrected, 0.0, 1.0) * 255);
            }
        }

        // Direct pixel iteration sidesteps SKColorFilter.CreateTable, which behaved oddly
        // in 3.116 when called repeatedly on freshly-allocated bitmaps (subsequent effects
        // in the chain would silently no-op). The lookup-table read is a single index op
        // per channel — fast enough at preview sizes (800×600 ≈ 1 ms).
        var table = _cachedTable;
        return ApplyPixelOperation(source, c =>
            new SKColor(table[c.Red], table[c.Green], table[c.Blue], c.Alpha));
    }
}
