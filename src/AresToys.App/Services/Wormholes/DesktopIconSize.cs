using Microsoft.Win32;

namespace AresToys.App.Services.Wormholes;

/// <summary>Resolves the user's current Windows desktop icon size — the value behind the
/// "Large icons / Medium icons / Small icons" submenu on the desktop right-click. We mirror
/// that as the default tile size in wormhole windows so a fresh wormhole reads "scaled like
/// the desktop" instead of using our own hard-coded default (which on a high-DPI 4K monitor
/// felt huge, and on a "Small icons" Surface looked tiny).
///
/// Source: <c>HKCU\Software\Microsoft\Windows\Shell\Bags\1\Desktop\IconSize</c>. Cached once
/// per process — the user can change desktop icon size mid-session but it's rare enough that
/// missing the update until next app launch is fine.</summary>
internal static class DesktopIconSize
{
    private const string KeyPath = @"Software\Microsoft\Windows\Shell\Bags\1\Desktop";
    private const string ValueName = "IconSize";
    private const int Fallback = 48;
    private const int Min = 24;
    private const int Max = 256;

    private static int? _cached;

    /// <summary>Current desktop icon size, clamped to a sane range (sub-24 px is unreadable,
    /// over-256 is past any Windows preset). Returns 48 if the registry value is missing or
    /// unreadable (locked-down environments, group policy weirdness).</summary>
    public static int Get()
    {
        if (_cached is { } cached) return cached;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key?.GetValue(ValueName) is int size && size > 0)
            {
                _cached = Math.Clamp(size, Min, Max);
                return _cached.Value;
            }
        }
        catch { /* registry access can fail under sandboxing or per-user reg unmounted */ }
        _cached = Fallback;
        return Fallback;
    }
}
