using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AresToys.Capture;

namespace AresToys.App.Services.Wormholes;

/// <summary>
/// Computes a stable identifier for the current monitor configuration. Used by
/// <see cref="WormholeStoreJson"/> to pick which positions file owns the live window
/// geometry: each distinct setup hash gets its own JSON, so switching configurations (RDP
/// connect with a small phone screen, plugging in a different external monitor, …) loads
/// a different layout instead of squashing the desktop one and losing the user's careful
/// placement on reconnect.
///
/// <para>Hash inputs are the per-monitor <c>(DeviceName, Width × Height)</c> pairs ordered
/// by virtual-screen position so reattaching the same monitors yields the same hash even
/// if the OS happened to enumerate them in a different order. <c>DeviceName</c> is the
/// stable <c>\\.\DISPLAY{n}</c> string Windows assigns to each display adapter output —
/// the same physical monitor reconnects to the same name across reboots when nothing else
/// in the topology changes.</para>
///
/// <para>The output is a 16-character lowercase hex prefix of the SHA-256 digest. 16 chars
/// = 64 bits of entropy; collision probability is negligible for the realistic universe of
/// "monitor configurations a single user owns" (a few dozen at most over a device's
/// lifetime) and the prefix keeps the on-disk filenames short enough to read at a glance
/// when troubleshooting.</para>
/// </summary>
public static class MonitorSetupIdentifier
{
    /// <summary>Computes the setup hash from the current display configuration. Pure read —
    /// no I/O, no state. Safe to call from any thread.</summary>
    public static string ComputeCurrentSetupHash()
        => ComputeSetupHash(MonitorEnumeration.Enumerate());

    /// <summary>Test seam: hash an arbitrary monitor list. Production code uses
    /// <see cref="ComputeCurrentSetupHash"/>; this overload lets unit tests pin the
    /// hashing contract against synthetic inputs without monkeying with the live display.</summary>
    public static string ComputeSetupHash(IReadOnlyList<MonitorInfo> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0) return "no-monitors";

        // Order by DeviceName (the stable Windows display identifier — \\.\DISPLAYn) so two
        // enumerations of the same physical hardware produce the same hash regardless of
        // both (a) the order EnumDisplayMonitors happened to walk the displays this run and
        // (b) the user rearranging which monitor is "left" vs "right" in Display Settings
        // — that rearrange shouldn't fragment a setup into two files. Resolution is the
        // tiebreaker on the unlikely event of duplicate DeviceNames.
        var ordered = monitors
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ThenBy(m => m.Width)
            .ThenBy(m => m.Height)
            .ToList();

        var sb = new StringBuilder();
        foreach (var m in ordered)
        {
            // Resolution only — NOT virtual-screen X/Y. Two monitors of the same model at
            // the same resolution should hash identically even if Windows arranges them
            // left-right vs right-left between sessions. Position-stability would over-
            // partition the setup space and create needless "new setup" files when the
            // user just dragged a monitor in Display Settings without changing hardware.
            sb.Append(m.Name).Append('|')
              .Append(m.Width.ToString(CultureInfo.InvariantCulture)).Append('x')
              .Append(m.Height.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        var input = Encoding.UTF8.GetBytes(sb.ToString());
        var digest = SHA256.HashData(input);
        // 16 chars = 64 bits prefix — see class doc for the rationale on this length.
        var hex = new StringBuilder(16);
        for (var i = 0; i < 8; i++) hex.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
        return hex.ToString();
    }
}
