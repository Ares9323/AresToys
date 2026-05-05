using System.Globalization;
using ShareQ.App.Resources;
using ShareQ.ImageEffects;

namespace ShareQ.App.Services;

/// <summary>UI-side translator for the ShareX-style effect catalog. The effect classes live in
/// ShareQ.ImageEffects (which has zero references to our resx) so we can't decorate them with
/// localisation attributes; instead the App-layer view models call these helpers when rendering
/// effect names, category headers, and parameter labels. Lookup precedence mirrors what the rest
/// of the i18n stack does — instance singleton culture → invariant fallback → caller-supplied
/// fallback (typically the raw English string the effect ships with).</summary>
public static class ImageEffectLocalizer
{
    private static CultureInfo Culture =>
        Markup.LocalizedStrings.Instance.Culture ?? CultureInfo.CurrentUICulture;

    /// <summary>Resolve an effect's display name from its <see cref="ImageEffect.Id"/>. Unknown
    /// ids fall back to <paramref name="fallback"/> (the original English Name) so a future
    /// .sxie-imported effect doesn't render as a raw key.</summary>
    public static string LocalizeEffect(string effectId, string fallback)
    {
        if (string.IsNullOrEmpty(effectId)) return fallback;
        var key = "Effect_" + effectId;
        return Strings.ResourceManager.GetString(key, Culture) ?? fallback;
    }

    public static string LocalizeCategory(ImageEffectCategory category)
    {
        var key = "EffectCategory_" + category;
        return Strings.ResourceManager.GetString(key, Culture) ?? category.ToString();
    }

    /// <summary>Lookup a parameter label by its CLR property name (treated as a global key —
    /// "Amount" / "Strength" / "Size" reused across many effects share one translation). Returns
    /// <paramref name="fallback"/> when the property has no entry, so effect-specific labels
    /// like "Mid: Cyan-Red" stay in English without bloating the resx.</summary>
    public static string LocalizeParameter(string propertyName, string fallback)
    {
        if (string.IsNullOrEmpty(propertyName)) return fallback;
        var key = "Param_" + propertyName;
        return Strings.ResourceManager.GetString(key, Culture) ?? fallback;
    }
}
