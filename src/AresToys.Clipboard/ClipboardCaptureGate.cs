using Microsoft.Extensions.Options;

namespace AresToys.Clipboard;

public sealed class ClipboardCaptureGate : IClipboardCaptureGate
{
    private readonly IForegroundProcessProbe _probe;
    private readonly IOptionsMonitor<CaptureGateOptions> _options;

    public ClipboardCaptureGate(IForegroundProcessProbe probe, IOptionsMonitor<CaptureGateOptions> options)
    {
        _probe = probe;
        _options = options;
    }

    public GateDecision Evaluate() => Evaluate(_probe.GetForeground());

    public GateDecision Evaluate(ForegroundSnapshot foreground)
    {
        var opts = _options.CurrentValue;
        if (opts.IncognitoActive) return new GateDecision(Allow: false, Reason: "incognito");

        var process = foreground.ProcessName;
        if (process is null) return new GateDecision(Allow: true, Reason: null);

        if (IsBlocked(opts.BlockedProcesses, process))
            return new GateDecision(Allow: false, Reason: $"blocked:{process}");

        return new GateDecision(Allow: true, Reason: null);
    }

    private static bool IsBlocked(IReadOnlyList<string> blocked, string actual)
    {
        var actualNorm = Strip(actual);
        foreach (var entry in blocked)
        {
            if (string.Equals(actualNorm, Strip(entry), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string Strip(string name)
        => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
}
