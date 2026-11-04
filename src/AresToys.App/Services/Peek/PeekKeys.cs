using Microsoft.Extensions.Logging;
using AresToys.App.Services.Hotkeys;
using AresToys.Core.Pipeline;
using AresToys.Hotkeys;
using AresToys.Storage.Settings;

namespace AresToys.App.Services.Peek;

/// <summary>The two configurable Peek keys: <see cref="Toggle"/> opens Peek in an Explorer file list (main app) and
/// closes it again (Peek window), <see cref="OpenAssigned"/> opens the current file in the program assigned to its
/// extension while Peek is open. <see cref="HotkeyBinding.Modifiers"/> holds <see cref="HotkeyModifiers"/> bits;
/// a binding may be a bare key (no modifiers).</summary>
public sealed record PeekKeys(HotkeyBinding Toggle, HotkeyBinding OpenAssigned)
{
    private const uint VkSpace = 0x20;
    private const uint VkE = 0x45;

    /// <summary>The default toggle, Ctrl+Space.</summary>
    public static HotkeyBinding DefaultToggle { get; } = new((int)HotkeyModifiers.Control, VkSpace);

    /// <summary>The default open key, E.</summary>
    public static HotkeyBinding DefaultOpenAssigned { get; } = new((int)HotkeyModifiers.None, VkE);

    public static PeekKeys Default { get; } = new(DefaultToggle, DefaultOpenAssigned);

    /// <summary>True when a key event with these modifiers and this virtual key is <paramref name="binding"/>.
    /// Both sides compare only the four modifier bits.</summary>
    public static bool Matches(HotkeyBinding binding, HotkeyModifiers modifiers, uint virtualKey)
        => binding.VirtualKey == virtualKey && Bits(binding.Modifiers) == Bits((int)modifiers);

    private static int Bits(int modifiers)
        => modifiers & (int)(HotkeyModifiers.Alt | HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Win);
}

/// <summary>Reads and writes the Peek keys in the settings store (keys <see cref="ToggleKey"/> and
/// <see cref="OpenKey"/>) as text such as <c>Ctrl + Space</c> or <c>E</c>, the format of
/// <see cref="HotkeyDisplay"/> and <see cref="KeyComboParser"/>. Nothing is cached: the satellite reads the keys at
/// each Peek open, the main app applies them through <see cref="Changed"/>.</summary>
public sealed class PeekKeysStore(ISettingsStore settings, ILogger<PeekKeysStore>? logger = null)
{
    public const string ToggleKey = "peek.hotkey";
    public const string OpenKey = "peek.open_key";

    /// <summary>Raised by <see cref="SaveAsync"/> with the saved keys, on the caller's thread. Only the main app
    /// listens (<see cref="PeekHotkey"/> re-binds).</summary>
    public event Action<PeekKeys>? Changed;

    /// <summary>The stored keys; a missing or invalid value gives its default (invalid data is logged).</summary>
    public async Task<PeekKeys> LoadAsync(CancellationToken ct = default)
    {
        var toggle = await ReadAsync(ToggleKey, PeekKeys.DefaultToggle, ct).ConfigureAwait(false);
        var open = await ReadAsync(OpenKey, PeekKeys.DefaultOpenAssigned, ct).ConfigureAwait(false);
        return new PeekKeys(toggle, open);
    }

    public async Task SaveAsync(PeekKeys keys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        await settings.SetAsync(ToggleKey, Format(keys.Toggle), sensitive: false, ct).ConfigureAwait(false);
        await settings.SetAsync(OpenKey, Format(keys.OpenAssigned), sensitive: false, ct).ConfigureAwait(false);
        Changed?.Invoke(keys);
    }

    /// <summary>The text of a binding, e.g. <c>Ctrl + Space</c>.</summary>
    public static string Format(HotkeyBinding binding)
        => HotkeyDisplay.Format((HotkeyModifiers)binding.Modifiers, binding.VirtualKey);

    private async Task<HotkeyBinding> ReadAsync(string key, HotkeyBinding fallback, CancellationToken ct)
    {
        string? raw;
        try
        {
            raw = await settings.GetAsync(key, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Peek keys: reading {Key} failed, using the default", key);
            return fallback;
        }

        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (KeyComboParser.Parse(raw) is { VirtualKey: not 0 } parsed)
            return new HotkeyBinding((int)parsed.Modifiers, parsed.VirtualKey);

        logger?.LogWarning("Peek keys: {Key} holds \"{Value}\", which is not a key, using the default", key, raw);
        return fallback;
    }
}
