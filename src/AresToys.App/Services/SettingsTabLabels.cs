namespace AresToys.App.Services;

/// <summary>Maps <see cref="ViewModels.SettingsTab"/> raw names (lowercased so they round-trip
/// cleanly through JSON config) to the user-facing labels the sidebar prints. Used by the
/// OpenSettingsTask "tab" parameter dropdown so the user picks "Hotkeys &amp; workflows" instead
/// of having to remember the internal "hotkeys" raw code.</summary>
internal static class SettingsTabLabels
{
    public static string LabelFor(string raw) => raw.ToLowerInvariant() switch
    {
        "uploaders"  => "Uploaders",
        "hotkeys"    => "Hotkeys & workflows",
        "capture"    => "Capture",
        "theme"      => "Theme",
        "categories" => "Clipboard categories",
        "wormholes"  => "Wormholes",
        "settings"   => "App settings",
        "debug"      => "Debug",
        "about"      => "About",
        _            => raw,
    };
}
