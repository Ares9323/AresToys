using Microsoft.Extensions.Logging;
using AresToys.App.Services.Hotkeys;
using AresToys.Core.Pipeline;
using AresToys.Hotkeys;

namespace AresToys.App.Services.Pipeline;

/// <summary>Concrete <see cref="ICancelHotkeyRegistry"/> backed by the app's
/// <see cref="IHotkeyManager"/>. Parses the user-supplied combo string (same
/// <c>"Ctrl + Shift + X"</c> format <c>HotkeyDisplay</c> emits) via
/// <see cref="KeyComboParser"/>, registers a fresh hotkey under a unique id, and exposes a
/// <see cref="IDisposable"/> that detaches the handler + unregisters on the executor's
/// finally block. Failure (already-taken combo, Win32 refusal, unparseable string) silently
/// returns null — the loop simply runs without a cancel shortcut.</summary>
internal sealed class CancelHotkeyRegistry : ICancelHotkeyRegistry
{
    private readonly IHotkeyManager _hotkeys;
    private readonly ILogger<CancelHotkeyRegistry> _logger;
    private int _idCounter;

    public CancelHotkeyRegistry(IHotkeyManager hotkeys, ILogger<CancelHotkeyRegistry> logger)
    {
        _hotkeys = hotkeys;
        _logger = logger;
    }

    public IDisposable? Register(string? combo, Action onTriggered)
    {
        ArgumentNullException.ThrowIfNull(onTriggered);
        var parsed = KeyComboParser.Parse(combo);
        if (parsed is null) return null;
        if (parsed.Value.VirtualKey == 0) return null;

        // Unique id per registration so multiple concurrent Repeat steps (rare but possible —
        // a workflow could itself be triggered while another runs) don't collide.
        var id = $"pipeline-cancel-{Interlocked.Increment(ref _idCounter)}";
        var def = new HotkeyDefinition(id, parsed.Value.Modifiers, parsed.Value.VirtualKey);

        EventHandler<HotkeyTriggeredEventArgs>? handler = null;
        handler = (_, e) =>
        {
            // Filter by id so we only react to OUR hotkey, not every fired key in the manager.
            if (string.Equals(e.Definition.Id, id, StringComparison.Ordinal))
            {
                try { onTriggered(); }
                catch (Exception ex) { _logger.LogWarning(ex, "CancelHotkey callback threw"); }
            }
        };
        _hotkeys.Triggered += handler;

        if (!_hotkeys.Register(def))
        {
            // Win32 refused — combo already taken by another app / hotkey. Detach + give up.
            _hotkeys.Triggered -= handler;
            _logger.LogInformation("CancelHotkey: registration refused for combo '{Combo}' (probably already bound)", combo);
            return null;
        }
        _logger.LogDebug("CancelHotkey: registered '{Combo}' as {Id}", combo, id);

        return new Releaser(() =>
        {
            _hotkeys.Triggered -= handler;
            _hotkeys.Unregister(id);
            _logger.LogDebug("CancelHotkey: released {Id}", id);
        });
    }

    /// <summary>Tiny IDisposable that runs the supplied action exactly once. The executor
    /// disposes us in its <c>finally</c>; we don't want a double-unregister if something
    /// double-disposes by accident.</summary>
    private sealed class Releaser : IDisposable
    {
        private Action? _action;
        public Releaser(Action action) { _action = action; }
        public void Dispose()
        {
            var a = Interlocked.Exchange(ref _action, null);
            a?.Invoke();
        }
    }
}
