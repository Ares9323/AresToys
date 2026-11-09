using SkiaSharp;

namespace AresToys.ImageEffects;

/// <summary>Base class for every image effect. <see cref="Id"/> is the stable identifier used
/// in JSON serialisation (matches ShareX's id strings so .sxie presets round-trip), <see cref="Name"/>
/// is the display label. Subclasses expose tunable parameters as plain public properties — the
/// preset serializer reflects over them, so anything `[JsonIgnore]`-able stays out of the file
/// without ceremony.</summary>
public abstract class ImageEffect
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract ImageEffectCategory Category { get; }

    /// <summary>Run the effect. Implementations MUST NOT mutate <paramref name="source"/> — the
    /// preset chain feeds the same input to multiple previews and to the final pipeline render.</summary>
    public abstract SKBitmap Apply(SKBitmap source);
}
