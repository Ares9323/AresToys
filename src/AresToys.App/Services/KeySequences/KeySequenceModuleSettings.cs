using System.Text.Json;
using Microsoft.Extensions.Logging;
using AresToys.Storage.Settings;

namespace AresToys.App.Services.KeySequences;

public enum OverlayPositionMode
{
    /// <summary>Spawn just below the mouse cursor.</summary>
    MouseCursor,
    /// <summary>Spawn at the position the user last dragged the overlay to (recorded on close).
    /// Falls back to <see cref="FixedCenter"/> on first run when no last position is recorded yet.</summary>
    LastPosition,
    /// <summary>Top-center of the active monitor's work area, with a small top margin.</summary>
    FixedTop,
    /// <summary>Default. Center of the active monitor's work area.</summary>
    FixedCenter,
    /// <summary>Bottom-center of the active monitor's work area, with a small bottom margin.</summary>
    FixedBottom,
    /// <summary>Best-effort: anchor to the right edge of the focused control's text caret via
    /// Win32 <c>GetGUIThreadInfo.rcCaret</c>. Works for classic Win32 edit controls (WinForms,
    /// pre-WinUI Office, Qt apps like Telegram desktop). Fails silently in Chromium / Electron /
    /// WinUI apps (UWP-style Notepad in Win11 included). Falls back to <see cref="FixedCenter"/>
    /// when the lookup fails.</summary>
    CaretRight,
    /// <summary>Like <see cref="CaretRight"/> but anchors ABOVE the caret line so the overlay
    /// doesn't sit on top of the text the user is still seeing. Same fallback rules.</summary>
    CaretTop,
}

/// <summary>
/// Strongly-typed projection of the module's user-tweakable runtime settings (overlay position,
/// confirm key, app blacklist). The master enable/disable lives in <see cref="ModuleSettings"/>
/// (the same place as Clipboard / Launcher / Wormholes) — toggling the module on/off requires a
/// restart, consistent with the other modules. Position / ConfirmKey / Blacklist changes ARE
/// hot-reloadable and raise <see cref="Changed"/>.
/// </summary>
public sealed class KeySequenceModuleSettings
{
    public const string KeyPosition = "keysequences.overlay.position";
    public const string KeyLastX = "keysequences.overlay.last-x";
    public const string KeyLastY = "keysequences.overlay.last-y";
    public const string KeyConfirmVk = "keysequences.overlay.confirm-vk";
    public const string KeyBlacklist = "keysequences.blacklist";

    /// <summary>Default process-name blacklist. Password managers + RDP + common VM hypervisors.
    /// Matched case-insensitively against the foreground process name (with or without ".exe").</summary>
    public static readonly IReadOnlyList<string> DefaultBlacklist = new[]
    {
        "KeePass.exe",
        "1Password.exe",
        "Bitwarden.exe",
        "mstsc.exe",
        "vmware-vmx.exe",
        "VirtualBox.exe",
    };

    public const uint DefaultConfirmVk = 0x0D; // VK_RETURN

    private readonly ISettingsStore _settings;
    private readonly ILogger<KeySequenceModuleSettings> _logger;

    public KeySequenceModuleSettings(ISettingsStore settings, ILogger<KeySequenceModuleSettings> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public OverlayPositionMode Position { get; private set; } = OverlayPositionMode.FixedCenter;
    /// <summary>Last screen-space position where the user dragged the overlay before it closed.
    /// Both nullable: <c>null</c> means "no drag yet on this machine" — the spawn logic falls
    /// back to <see cref="OverlayPositionMode.CenterScreen"/> in that case.</summary>
    public int? LastX { get; private set; }
    public int? LastY { get; private set; }
    public uint ConfirmVk { get; private set; } = DefaultConfirmVk;
    public IReadOnlyList<string> Blacklist { get; private set; } = DefaultBlacklist;

    public event EventHandler? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var posStr = await _settings.GetAsync(KeyPosition, cancellationToken).ConfigureAwait(false);
        Position = posStr switch
        {
            "MouseCursor" => OverlayPositionMode.MouseCursor,
            "LastPosition" => OverlayPositionMode.LastPosition,
            "FixedTop" => OverlayPositionMode.FixedTop,
            "FixedBottom" => OverlayPositionMode.FixedBottom,
            "CaretRight" or "Caret" => OverlayPositionMode.CaretRight, // accepts legacy "Caret" name
            "CaretTop" => OverlayPositionMode.CaretTop,
            _ => OverlayPositionMode.FixedCenter, // default + back-compat for "FixedScreen"
        };

        LastX = int.TryParse(await _settings.GetAsync(KeyLastX, cancellationToken).ConfigureAwait(false), out var x) ? x : null;
        LastY = int.TryParse(await _settings.GetAsync(KeyLastY, cancellationToken).ConfigureAwait(false), out var y) ? y : null;

        var vkStr = await _settings.GetAsync(KeyConfirmVk, cancellationToken).ConfigureAwait(false);
        ConfirmVk = uint.TryParse(vkStr, out var v) ? v : DefaultConfirmVk;

        var blJson = await _settings.GetAsync(KeyBlacklist, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(blJson))
        {
            try { Blacklist = JsonSerializer.Deserialize<List<string>>(blJson) ?? DefaultBlacklist.ToList(); }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "KeySequenceModuleSettings: blacklist JSON corrupt — using defaults.");
                Blacklist = DefaultBlacklist;
            }
        }
        else
        {
            Blacklist = DefaultBlacklist;
        }
    }

    public async Task ApplyAsync(
        OverlayPositionMode position,
        uint confirmVk,
        IReadOnlyList<string> blacklist,
        CancellationToken cancellationToken)
    {
        Position = position;
        ConfirmVk = confirmVk == 0 ? DefaultConfirmVk : confirmVk;
        Blacklist = blacklist ?? DefaultBlacklist;

        await _settings.SetAsync(KeyPosition, position.ToString(), sensitive: false, cancellationToken).ConfigureAwait(false);
        await _settings.SetAsync(KeyConfirmVk, ConfirmVk.ToString(System.Globalization.CultureInfo.InvariantCulture), sensitive: false, cancellationToken).ConfigureAwait(false);
        await _settings.SetAsync(KeyBlacklist, JsonSerializer.Serialize(Blacklist), sensitive: false, cancellationToken).ConfigureAwait(false);

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Persist the screen-space position the user just dragged the overlay to. Called
    /// from <see cref="SequenceOverlayHost.Close"/> so the next spawn in
    /// <see cref="OverlayPositionMode.LastPosition"/> mode lands where the user left it.</summary>
    public async Task SaveLastPositionAsync(int x, int y, CancellationToken cancellationToken)
    {
        LastX = x;
        LastY = y;
        await _settings.SetAsync(KeyLastX, x.ToString(System.Globalization.CultureInfo.InvariantCulture), sensitive: false, cancellationToken).ConfigureAwait(false);
        await _settings.SetAsync(KeyLastY, y.ToString(System.Globalization.CultureInfo.InvariantCulture), sensitive: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Persist + apply the overlay position mode. Hot-reloadable: the next overlay spawn
    /// reads the new value directly from this object (the host queries <see cref="Position"/>
    /// on every <c>PositionOverlay</c> call), so no restart is required for the change to land.</summary>
    public async Task SetPositionAsync(OverlayPositionMode mode, CancellationToken cancellationToken)
    {
        if (Position == mode) return;
        Position = mode;
        await _settings.SetAsync(KeyPosition, mode.ToString(), sensitive: false, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
