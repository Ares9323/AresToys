namespace AresToys.ImageEffects.Filters;

/// <summary>Common base for "filter" effects — global stylings that re-render the source
/// rather than transform its geometry. Tags the category so the picker UI groups them
/// automatically.</summary>
public abstract class FilterImageEffectBase : ImageEffect
{
    public sealed override ImageEffectCategory Category => ImageEffectCategory.Filters;
}
