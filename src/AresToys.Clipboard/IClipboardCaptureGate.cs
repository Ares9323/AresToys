namespace AresToys.Clipboard;

public interface IClipboardCaptureGate
{
    /// <summary>Returns true if the current clipboard event should be ingested. Looks the foreground window up itself.</summary>
    GateDecision Evaluate();

    /// <summary>Same decision for a foreground window already looked up by the caller (taken once per event).</summary>
    GateDecision Evaluate(ForegroundSnapshot foreground);
}

public sealed record GateDecision(bool Allow, string? Reason);
