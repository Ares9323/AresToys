namespace AresToys.ImageEffects.Manipulations;

/// <summary>Common base for size / shape transforms — resize, crop, canvas, rounded corners,
/// shadow, etc. Tags the category so consumers can group these in the UI without each
/// effect having to repeat the override.</summary>
public abstract class ManipulationImageEffectBase : ImageEffect
{
    public sealed override ImageEffectCategory Category => ImageEffectCategory.Manipulations;
}
