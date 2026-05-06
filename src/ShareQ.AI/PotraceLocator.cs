namespace ShareQ.AI;

/// <summary>Resolve the absolute path to the bundled <c>potrace.exe</c>. Mirrors the
/// pattern used by <c>FfmpegLocator</c>: probe the AppContext.BaseDirectory's
/// <c>Tools/potrace.exe</c> first (where the .csproj copies it), fall back to a sibling
/// directory of the entry assembly. Returns null if the binary isn't present so callers
/// can degrade gracefully (skip the trace task / hide the editor button).</summary>
public static class PotraceLocator
{
    /// <summary>Probe well-known locations for potrace.exe and return the first one that
    /// exists. Returns null when none of them resolve — caller treats that as "feature
    /// unavailable" rather than crashing.</summary>
    public static string? Find()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Tools", "potrace.exe"),
            Path.Combine(AppContext.BaseDirectory, "potrace.exe"),
            Path.Combine(Path.GetDirectoryName(typeof(PotraceLocator).Assembly.Location) ?? "", "Tools", "potrace.exe"),
        };
        foreach (var path in candidates)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
        }
        return null;
    }
}
