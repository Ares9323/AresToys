using SkiaSharp;

namespace AresToys.ImageEffects;

/// <summary>An ordered chain of <see cref="ImageEffect"/> instances applied in sequence to a
/// source bitmap. Disabled entries (<see cref="EffectPresetEntry.Enabled"/> = false) are
/// skipped without removing them — same UX as ShareX, where you toggle effects on/off without
/// losing their parameters.</summary>
public sealed class EffectPreset
{
    /// <summary>Stable identifier — used by the pipeline task to reference a preset by id even
    /// after the user renames it. Generated as a GUID-string at preset creation.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public List<EffectPresetEntry> Effects { get; set; } = new();

    /// <summary>Run every enabled effect in order. The input bitmap is NOT mutated; if the chain
    /// is empty / all-disabled, a defensive copy is returned so callers can dispose without
    /// affecting the source.</summary>
    public SKBitmap Apply(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);
        SKBitmap current = source.Copy();
        foreach (var entry in Effects)
        {
            if (!entry.Enabled || entry.Effect is null) continue;
            var next = entry.Effect.Apply(current);
            current.Dispose();
            current = next;
        }
        return current;
    }
}

public sealed class EffectPresetEntry
{
    public bool Enabled { get; set; } = true;
    public ImageEffect? Effect { get; set; }

    public EffectPresetEntry() { }
    public EffectPresetEntry(ImageEffect effect, bool enabled = true)
    {
        Effect = effect;
        Enabled = enabled;
    }
}
