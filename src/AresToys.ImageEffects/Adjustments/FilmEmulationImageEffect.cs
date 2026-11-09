using AresToys.ImageEffects.Parameters;
using SkiaSharp;

namespace AresToys.ImageEffects.Adjustments;

public enum FilmEmulationPreset { Kodachrome, Velvia, Portra, FujiNeopan, IlfordHP5 }

/// <summary>Film emulation — discrete preset selector that approximates classic film stocks
/// via colour-matrix pre-grading. Placeholder: ShareX uses LUTs for accurate rendering; we
/// fall back to four hand-tuned matrices that give the right "vibe" without LUT data.</summary>
public sealed class FilmEmulationImageEffect : AdjustmentImageEffectBase
{
    public override string Id => "film_emulation";
    public override string Name => "Film emulation";

    public FilmEmulationPreset Preset { get; set; } = FilmEmulationPreset.Kodachrome;

    [EffectParameter(0, 100, DisplayName = "Strength")]
    public float Strength { get; set; } = 100f;

    public override SKBitmap Apply(SKBitmap source)
    {
        var s = Math.Clamp(Strength, 0f, 100f) / 100f;
        if (s <= 0) return source.Copy();
        var matrix = Preset switch
        {
            FilmEmulationPreset.Kodachrome => new float[] { 1.2f, 0, 0, 0, 0,   0, 1.05f, 0, 0, 0,   0, 0, 0.85f, 0, 0,   0, 0, 0, 1, 0 },
            FilmEmulationPreset.Velvia => new float[] { 1.3f, 0, 0, 0, 0,   0, 1.15f, 0, 0, 0,   0, 0, 1.1f, 0, 0,   0, 0, 0, 1, 0 },
            FilmEmulationPreset.Portra => new float[] { 1.05f, 0, 0, 0, 0.02f,   0, 1.0f, 0, 0, 0.02f,   0, 0, 0.95f, 0, 0,   0, 0, 0, 1, 0 },
            FilmEmulationPreset.FujiNeopan => new float[] { 0.3f, 0.6f, 0.1f, 0, 0,   0.3f, 0.6f, 0.1f, 0, 0,   0.3f, 0.6f, 0.1f, 0, 0,   0, 0, 0, 1, 0 },
            FilmEmulationPreset.IlfordHP5 => new float[] { 0.25f, 0.65f, 0.1f, 0, 0,   0.25f, 0.65f, 0.1f, 0, 0,   0.25f, 0.65f, 0.1f, 0, 0,   0, 0, 0, 1, 0 },
            _ => new float[] { 1, 0, 0, 0, 0,   0, 1, 0, 0, 0,   0, 0, 1, 0, 0,   0, 0, 0, 1, 0 },
        };
        return ApplyColorMatrix(source, matrix);
    }
}
