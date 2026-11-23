using System.Runtime.InteropServices;
using System.Windows.Interop;
using AresToys.App.Services.Hotkeys;
using AresToys.Hotkeys;
using Microsoft.Extensions.Logging;

namespace AresToys.App.Services;

/// <summary>Tells the user why the AresToys shortcuts do nothing while a window that runs as administrator is in front.
/// The low-level keyboard hook that serves every shortcut is blind then (UIPI), so without this the keys just vanish.
/// Each Win shortcut (and Print Screen) is also registered with RegisterHotKey on a message-only window: WM_HOTKEY still
/// arrives across UIPI. While AresToys can see the keys its hook handles them first and the hotkey never fires; when
/// it does fire and the foreground window is out of reach, <see cref="ElevatedWindowWarning"/> shows its notice (once
/// per session, silenced for 30 days on "Don't show again"). Nothing else runs: the owner picked "warning only".
/// Combos without Win are not registered: that would take them away from the elevated app. Win+V and Win+Shift+S
/// cannot be registered (Explorer holds them), so they stay Windows' own with no notice; reading the key state with
/// GetAsyncKeyState instead was tried and UIPI blocks it too. Not used when AresToys is elevated.</summary>
public sealed class ElevatedShortcutFallback : IDisposable
{
    private const int FirstId = 0xA000;
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkSnapshot = 0x2C;

    private readonly HotkeyConfigService _hotkeys;
    private readonly ElevatedWindowWarning _warning;
    private readonly ILogger<ElevatedShortcutFallback> _logger;
    private readonly Dictionary<string, int> _idsByShortcut = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _names = new();
    private readonly HashSet<int> _registered = new();
    private Func<string, bool> _isEnabled = _ => true;
    private HwndSource? _window;

    public ElevatedShortcutFallback(HotkeyConfigService hotkeys, ElevatedWindowWarning warning, ILogger<ElevatedShortcutFallback> logger)
    {
        _hotkeys = hotkeys;
        _warning = warning;
        _logger = logger;
    }

    /// <summary>Registers the Win shortcuts whose module is on (<paramref name="isEnabled"/>) and follows rebinding.
    /// UI thread, once, after the hook registrations.</summary>
    public async Task StartAsync(Func<string, bool> isEnabled, CancellationToken cancellationToken)
    {
        if (ElevationService.IsCurrentProcessElevated || _window is not null) return;
        _isEnabled = isEnabled;
        _window = new HwndSource(new HwndSourceParameters("AresToys elevated shortcut notice") { ParentWindow = new IntPtr(-3) });
        _window.AddHook(WndProc);

        var next = FirstId;
        foreach (var entry in await _hotkeys.GetCatalogAsync(cancellationToken))
        {
            var id = next++;
            _idsByShortcut[entry.Id] = id;
            _names[id] = entry.DisplayName;
            var definition = await _hotkeys.GetEffectiveAsync(entry.Id, cancellationToken);
            Set(entry.Id, definition.Modifiers, definition.VirtualKey);
        }

        _hotkeys.Changed += OnHotkeyChanged;
        _logger.LogInformation("Shortcut notice for elevated windows: {Count} shortcuts registered", _registered.Count);
    }

    private void Set(string shortcutId, HotkeyModifiers modifiers, uint virtualKey)
    {
        if (_window is null || !_idsByShortcut.TryGetValue(shortcutId, out var id)) return;
        if (_registered.Remove(id)) UnregisterHotKey(_window.Handle, id);

        if (virtualKey == 0 || !_isEnabled(shortcutId)) return;
        if ((modifiers & HotkeyModifiers.Win) == 0 && virtualKey != VkSnapshot) return;

        var flags = (uint)(modifiers & (HotkeyModifiers.Alt | HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Win)) | ModNoRepeat;
        if (RegisterHotKey(_window.Handle, id, flags, virtualKey)) _registered.Add(id);
        else _logger.LogDebug("Shortcut notice: {Shortcut} not registered (error {Error}), probably held by Windows", shortcutId, Marshal.GetLastWin32Error());
    }

    private void OnHotkeyChanged(object? sender, HotkeyDefinition definition)
    {
        _window?.Dispatcher.InvokeAsync(() => Set(definition.Id, definition.Modifiers, definition.VirtualKey));
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != WmHotkey || !_names.TryGetValue((int)wParam, out var name)) return 0;
        handled = true;
        var foreground = AresToys.App.Native.AppNativeMethods.GetForegroundWindow();
        if (foreground != 0 && ElevatedWindowWarning.IsOutOfReach(foreground))
            _ = _warning.WarnAsync(name, CancellationToken.None);
        return 0;
    }

    public void Dispose()
    {
        _hotkeys.Changed -= OnHotkeyChanged;
        if (_window is null) return;
        foreach (var id in _registered) UnregisterHotKey(_window.Handle, id);
        _registered.Clear();
        _window.Dispose();
        _window = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);
}
