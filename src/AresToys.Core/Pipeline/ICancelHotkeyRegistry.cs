namespace AresToys.Core.Pipeline;

/// <summary>Pipeline-level abstraction for registering a temporary global hotkey that breaks
/// out of an in-flight loop / wait. Lets <c>PipelineExecutor</c> wire a "cancel Repeat" combo
/// without coupling to Win32 / <c>IHotkeyManager</c> directly — the Pipeline assembly can't
/// reference Hotkeys for layering reasons, but it can resolve this Core-level interface from
/// <see cref="PipelineContext.Services"/> at runtime. The App layer provides the concrete
/// implementation that parses the combo string (via <c>KeyComboParser</c>) and registers via
/// <c>IHotkeyManager</c>. Disposing the returned token unregisters the hotkey + detaches the
/// handler — caller disposes when the loop exits so the combo stops being a system-wide capture.
/// </summary>
public interface ICancelHotkeyRegistry
{
    /// <summary>Register a temporary hotkey that fires <paramref name="onTriggered"/> on press.
    /// Returns null when <paramref name="combo"/> is empty / unparseable / Win32 refuses (the
    /// combo is already taken by another app or hotkey). Caller treats null as "no cancel
    /// hotkey, the loop runs to completion or until the outer token cancels".</summary>
    IDisposable? Register(string? combo, Action onTriggered);
}
