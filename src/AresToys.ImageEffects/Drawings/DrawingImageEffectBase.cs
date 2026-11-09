namespace AresToys.ImageEffects.Drawings;

/// <summary>Common base for "draw something on top / behind the image" effects — background
/// fills, watermarks, borders, gradient overlays. Tags the category so the picker UI groups
/// them under "Drawings" automatically.</summary>
public abstract class DrawingImageEffectBase : ImageEffect
{
    public sealed override ImageEffectCategory Category => ImageEffectCategory.Drawings;
}
