namespace AresToys.App.Services.Satellite;

/// <summary>What the main app told the satellite on its command line: which modules to host and which process to
/// follow (the satellite leaves when it is gone).</summary>
public sealed record SatelliteArgs(IReadOnlySet<string> Modules, int MainPid)
{
    /// <summary>True only when <c>--satellite</c> is present. <c>--modules=a,b</c> gives lower case trimmed names,
    /// <c>--main-pid=N</c> the process id (0 when absent or not a number).</summary>
    public static bool TryParse(string[] args, out SatelliteArgs? parsed)
    {
        parsed = null;
        if (!args.Any(a => string.Equals(a, SatelliteProtocol.Arg, StringComparison.OrdinalIgnoreCase))) return false;

        var modules = new HashSet<string>(StringComparer.Ordinal);
        var mainPid = 0;
        foreach (var arg in args)
        {
            if (arg.StartsWith(SatelliteProtocol.ModulesPrefix, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var name in arg[SatelliteProtocol.ModulesPrefix.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    modules.Add(name.ToLowerInvariant());
            }
            else if (arg.StartsWith(SatelliteProtocol.MainPidPrefix, StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(arg[SatelliteProtocol.MainPidPrefix.Length..], out var pid))
            {
                mainPid = pid;
            }
        }

        parsed = new SatelliteArgs(modules, mainPid);
        return true;
    }
}
