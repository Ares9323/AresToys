using System.Globalization;
using AresToys.App.Resources;

namespace AresToys.App.ViewModels;

/// <summary>One curated colour preset the user can apply with a single click. Selecting a preset
/// in the Theme tab fills every hex field at once and persists via the existing TryApply path —
/// no separate plumbing. Add new presets here by appending to <see cref="All"/>; the ComboBox
/// picks them up automatically.
///
/// <see cref="Name"/> stays in English so existing code paths that compare presets by name keep
/// working; the UI binds to <see cref="DisplayName"/>, which flips through the resx based on
/// the current culture.</summary>
public sealed record ThemePreset(
    string Name,
    string AccentForegroundLightHex,
    string AccentForegroundDarkHex,
    string AccentBackgroundLightHex,
    string AccentBackgroundDarkHex,
    string AccentDangerHex,
    string Surface1Hex,
    string Surface2Hex,
    string Surface3Hex,
    string OuterBorderHex,
    string InnerBorderHex)
{
    /// <summary>Localised name used in the Theme tab ComboBox. Falls back to <see cref="Name"/>
    /// when the preset isn't in the localiser map (e.g. a user-added preset). Looked up live so
    /// the dropdown picks up the user's chosen language without rebuilding the collection.</summary>
    public string DisplayName => ThemePresets.LocalizeName(Name);
}

public static class ThemePresets
{
    /// <summary>Sentinel "no preset" entry used as the initial ComboBox selection so opening the
    /// tab doesn't snap-apply a preset over the user's current colours.</summary>
    public static readonly ThemePreset Custom = new(
        Name: "Custom (current values)",
        AccentForegroundLightHex: string.Empty,
        AccentForegroundDarkHex: string.Empty,
        AccentBackgroundLightHex: string.Empty,
        AccentBackgroundDarkHex: string.Empty,
        AccentDangerHex: string.Empty,
        Surface1Hex: string.Empty,
        Surface2Hex: string.Empty,
        Surface3Hex: string.Empty,
        OuterBorderHex: string.Empty,
        InnerBorderHex: string.Empty);

    public static readonly ThemePreset Default = new(
        Name: "AresToys default",
        AccentForegroundLightHex: "#E5E6E6",
        AccentForegroundDarkHex: "#878787",
        AccentBackgroundLightHex: "#6BA780",
        AccentBackgroundDarkHex: "#314D3B",
        AccentDangerHex: "#8F2720",
        Surface1Hex: "#1A1A1A",
        Surface2Hex: "#1F1F1F",
        Surface3Hex: "#2D2D2D",
        OuterBorderHex: "#4A4A4A",
        InnerBorderHex: "#2D2D2D");

    public static readonly ThemePreset WizardBlue = new(
        Name: "Wizard blue",
        AccentForegroundLightHex: "#FFFFFF",
        AccentForegroundDarkHex: "#7592AE",
        AccentBackgroundLightHex: "#2B5278",
        AccentBackgroundDarkHex: "#142F4B",
        AccentDangerHex: "#A04040",
        Surface1Hex: "#111820",
        Surface2Hex: "#17212B",
        Surface3Hex: "#1E2C39",
        OuterBorderHex: "#3F4D5E",
        InnerBorderHex: "#1E2C39");

    public static readonly ThemePreset SorcererPurple = new(
        Name: "Sorcerer Purple",
        AccentForegroundLightHex: "#FFFFFF",
        AccentForegroundDarkHex: "#DCA5DD",
        AccentBackgroundLightHex: "#6B266B",
        AccentBackgroundDarkHex: "#511A4C",
        AccentDangerHex: "#99342F",
        Surface1Hex: "#201A1F",
        Surface2Hex: "#2B1E2B",
        Surface3Hex: "#392839",
        OuterBorderHex: "#5E3F5E",
        InnerBorderHex: "#391E39");

    public static readonly ThemePreset DruidGreen = new(
        Name: "Druid Green",
        AccentForegroundLightHex: "#FFFFFF",
        AccentForegroundDarkHex: "#7FAE75",
        AccentBackgroundLightHex: "#38782B",
        AccentBackgroundDarkHex: "#194C16",
        AccentDangerHex: "#673726",
        Surface1Hex: "#182017",
        Surface2Hex: "#232B21",
        Surface3Hex: "#2B3928",
        OuterBorderHex: "#3F5E3F",
        InnerBorderHex: "#3F5E3F");

    public static readonly ThemePreset WarlockRed = new(
        Name: "Warlock Red",
        AccentForegroundLightHex: "#FFFFFF",
        AccentForegroundDarkHex: "#C98787",
        AccentBackgroundLightHex: "#782B2B",
        AccentBackgroundDarkHex: "#541E1E",
        AccentDangerHex: "#A02727",
        Surface1Hex: "#201111",
        Surface2Hex: "#2B1717",
        Surface3Hex: "#391E1E",
        OuterBorderHex: "#5E3F3F",
        InnerBorderHex: "#5E3F3F");

    public static readonly ThemePreset ClericGold = new(
        Name: "Cleric Gold",
        AccentForegroundLightHex: "#F4F0EB",
        AccentForegroundDarkHex: "#C6B185",
        AccentBackgroundLightHex: "#9A803E",
        AccentBackgroundDarkHex: "#745F22",
        AccentDangerHex: "#952A2A",
        Surface1Hex: "#201D11",
        Surface2Hex: "#2B2417",
        Surface3Hex: "#39301E",
        OuterBorderHex: "#5E523F",
        InnerBorderHex: "#5E523F");

    public static readonly ThemePreset BardRouge = new(
        Name: "Bard Rouge",
        AccentForegroundLightHex: "#F4F0EB",
        AccentForegroundDarkHex: "#D08CBB",
        AccentBackgroundLightHex: "#9D428B",
        AccentBackgroundDarkHex: "#803671",
        AccentDangerHex: "#952A4A",
        Surface1Hex: "#3F1C32",
        Surface2Hex: "#4B2637",
        Surface3Hex: "#613650",
        OuterBorderHex: "#754467",
        InnerBorderHex: "#754467");

    public static readonly ThemePreset BurnMyEyes = new(
        Name: "Burn My Eyes",
        AccentForegroundLightHex: "#000000",
        AccentForegroundDarkHex: "#616161",
        AccentBackgroundLightHex: "#B7E5C7",
        AccentBackgroundDarkHex: "#6BA780",
        AccentDangerHex: "#ED5C52",
        Surface1Hex: "#C9C9C9",
        Surface2Hex: "#E4E4E4",
        Surface3Hex: "#FFFFFF",
        OuterBorderHex: "#9A9A9A",
        InnerBorderHex: "#C9C9C9");

    public static readonly IReadOnlyList<ThemePreset> All = [Custom, Default, BardRouge, ClericGold, DruidGreen, SorcererPurple, WarlockRed, WizardBlue, BurnMyEyes];

    /// <summary>English-name → resx key map. Custom presets the user might invent at some
    /// future point fall through unchanged.</summary>
    private static readonly Dictionary<string, string> ResourceKeys = new(StringComparer.Ordinal)
    {
        ["Custom (current values)"] = nameof(Strings.ThemePreset_Custom),
        ["AresToys default"]          = nameof(Strings.ThemePreset_Default),
        ["Wizard blue"]             = nameof(Strings.ThemePreset_WizardBlue),
        ["Sorcerer Purple"]         = nameof(Strings.ThemePreset_SorcererPurple),
        ["Druid Green"]             = nameof(Strings.ThemePreset_DruidGreen),
        ["Warlock Red"]             = nameof(Strings.ThemePreset_WarlockRed),
        ["Cleric Gold"]             = nameof(Strings.ThemePreset_ClericGold),
        ["Bard Rouge"]              = nameof(Strings.ThemePreset_BardRouge),
        ["Burn My Eyes"]            = nameof(Strings.ThemePreset_BurnMyEyes),
    };

    public static string LocalizeName(string name)
    {
        if (!ResourceKeys.TryGetValue(name, out var key)) return name;
        var culture = Markup.LocalizedStrings.Instance.Culture ?? CultureInfo.CurrentUICulture;
        return Strings.ResourceManager.GetString(key, culture) ?? name;
    }
}
