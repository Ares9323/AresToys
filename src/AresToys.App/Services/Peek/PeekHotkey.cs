using System.Runtime.InteropServices;
using AresToys.App.Services.NewPlus;
using AresToys.App.Services.Satellite;
using AresToys.Hotkeys;
using Microsoft.Extensions.Logging;

namespace AresToys.App.Services.Peek;

/// <summary>The Peek shortcut (Ctrl+Space by default, see <see cref="PeekKeys"/>) in an Explorer window whose focus is in the file list, or on the desktop icons: asks the Peek module in the non elevated
/// satellite to preview the selected file. The main app only owns the key (its keyboard hook reaches elevated windows
/// too); reading the selection and showing the window happen in the satellite, whose Shell.Application sees a normal
/// Explorer. Active only while the Peek module is enabled and the satellite is connected, so with the module off the key
/// is never touched. The state follows <see cref="SatelliteService.ModulesApplied"/>, the one place that reads the
/// module switches.</summary>
public sealed class PeekHotkey
{
    private const string HookId = "peek.show";

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public int rcLeft, rcTop, rcRight, rcBottom;
    }

    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    private readonly KeyboardHook _hook;
    private readonly PeekKeysStore _keys;
    private readonly SatelliteService _satellite;
    private readonly PeekService _service;
    private readonly ILogger<PeekHotkey>? _logger;
    private volatile bool _active;

    public PeekHotkey(KeyboardHook hook, PeekKeysStore keys, SatelliteService satellite, PeekService service,
        ILogger<PeekHotkey>? logger = null)
    {
        _hook = hook;
        _keys = keys;
        _satellite = satellite;
        _service = service;
        _logger = logger;
        satellite.ModulesApplied += OnModulesApplied;
        // The key is bound once the stored shortcut is read; a change saved in Settings re-binds it at once.
        keys.Changed += Bind;
        _ = BindStoredAsync();
    }

    private async Task BindStoredAsync()
    {
        try
        {
            Bind(await _keys.LoadAsync().ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Peek: reading the shortcut failed, using the default");
            Bind(PeekKeys.Default);
        }
    }

    // Register replaces the binding with the same id, so a re-bind needs no unregister. The `when` condition stays
    // the same for any key: with a bare key such as Space it is what keeps typing elsewhere (and in the rename box
    // or address bar of Explorer) untouched.
    private void Bind(PeekKeys keys)
        => _hook.Register(HookId, (HotkeyModifiers)keys.Toggle.Modifiers, keys.Toggle.VirtualKey, OnPressed,
            suppress: true, when: CanHandle);

    /// <summary>Turns the shortcut on or off. Cheap.</summary>
    public void SetActive(bool active) => _active = active;

    private void OnModulesApplied(IReadOnlyList<string> modules)
        => SetActive(modules.Contains(SatelliteModuleNames.Peek, StringComparer.OrdinalIgnoreCase));

    // Hook thread: only cheap checks. The key is ours only in an Explorer window whose focus is in the file list, or on
    // the desktop whose focus is in its icon list (not the address bar, the search box or a rename box, where the same
    // key means something else) and only with a satellite to answer.
    private bool CanHandle()
    {
        if (!_active || _satellite.Server.Current is null) return false;
        var root = NewPlusExplorer.ForegroundRoot();
        var rootClass = NewPlusExplorer.ClassOf(root);
        var isDesktop = rootClass is "Progman" or "WorkerW";
        if (rootClass != "CabinetWClass" && !isDesktop) return false;
        var thread = GetWindowThreadProcessId(root, out _);
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (thread == 0 || !GetGUIThreadInfo(thread, ref info)) return false;
        var focus = NewPlusExplorer.ClassOf(info.hwndFocus);
        return isDesktop ? focus == "SysListView32" : focus is "DirectUIHWND" or "SysListView32";
    }

    // The key arrived here, so this process owns the foreground right: PeekService hands it to the satellite on this
    // thread before it sends the request.
    private void OnPressed() => _ = _service.ToggleAsync(NewPlusExplorer.ForegroundRoot());
}
