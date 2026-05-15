using System.Globalization;

namespace AresToys.Pipeline.Tasks;

/// <summary>ShareX-style date token expansion for capture sub-folder paths and similar
/// filename templates. Single source of truth — both <see cref="SaveToFileTask"/> and the
/// AresToys.App services (SaveSvgTask, RecordingCoordinator) call this, so a user's pattern
/// like <c>%y\%mon\%d</c> resolves identically across raster save, SVG save, and screen
/// recording.
///
/// Token order matters: longest-match-first. The previous per-site copies replaced
/// <c>%mo</c> before <c>%mon</c>, so <c>%mon</c> was eaten as <c>%mo</c> + literal <c>n</c>
/// and the user got filenames like <c>05n</c> instead of <c>May</c>. Same trap with
/// <c>%mi</c>/<c>%mo</c> if both were ever ordered short-first.
/// </summary>
public static class DatePatternExpander
{
    /// <summary>Replace all ShareX-style tokens in <paramref name="pattern"/> with their
    /// concrete values for <paramref name="now"/>. Unknown <c>%</c>-sequences pass through
    /// untouched.
    ///
    /// Numeric tokens (year/month-number/day/hour/minute/second) use
    /// <see cref="CultureInfo.InvariantCulture"/> so paths stay portable: a folder called
    /// <c>2026\05\15</c> reads the same on any OS regardless of the user's locale. The
    /// month-name token <c>%mon</c> is the only deliberately-localised piece — it follows the
    /// app UI language (set by <c>LocalizationService.ApplyToThread</c>, which writes both
    /// <see cref="Thread.CurrentCulture"/> and the process default), so an Italian user gets
    /// "Maggio" instead of "May" in their sub-folder names.</summary>
    public static string Expand(string pattern, DateTime now) => pattern
        .Replace("%yyyy", now.ToString("yyyy", CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("%yy",   now.ToString("yy",   CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("%y",    now.ToString("yyyy", CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("%mon",  now.ToString("MMMM", CultureInfo.CurrentCulture),   StringComparison.Ordinal)
        .Replace("%mo",   now.ToString("MM",   CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("%d",    now.ToString("dd",   CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("%h",    now.ToString("HH",   CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("%mi",   now.ToString("mm",   CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("%s",    now.ToString("ss",   CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("%pm",   now.ToString("tt",   CultureInfo.InvariantCulture), StringComparison.Ordinal);
}
