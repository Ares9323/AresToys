using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Extensions.Logging;
using AresToys.App.Native;
using AresToys.App.Services.Hotkeys;
using AresToys.Core.Pipeline;
using AresToys.Hotkeys;

namespace AresToys.App.Services.PipelineTasks;

/// <summary>
/// Sends a single keystroke or modifier+key combo (Ctrl+Shift+T, F12, Win+R, …) to the
/// foreground window via SendInput. Useful as a separator between paste-history-item steps
/// (Enter / Tab) AND as a general "send shortcut" remap — bind a hotkey to a workflow whose
/// only step is PressKeyTask{combo:"Ctrl+W"} to remap a side-button trigger to "close tab",
/// PowerToys-KBM style.
/// <para>
/// Config — three accepted shapes (checked in priority order):
/// <list type="bullet">
/// <item><c>{"combo":"Ctrl + Shift + T"}</c> — preferred. The workflow editor's hotkey
/// capture button produces this format; the parser is the inverse of
/// <see cref="HotkeyDisplay.Format"/>.</item>
/// <item><c>{"vk":86,"modifiers":2}</c> — structured form. <c>modifiers</c> is a bitmask of
/// <see cref="HotkeyModifiers"/> (Alt=1, Control=2, Shift=4, Win=8).</item>
/// <item><c>{"key":"enter"|"tab"}</c> — legacy single-key form kept for backward compat with
/// 0.1.16 / earlier "Press Enter" and "Press Tab" catalog presets.</item>
/// </list>
/// Optional knobs in all three modes: <c>"count"</c> (default 1) and <c>"delayMs"</c> (default
/// 0) — number of repeats and the wait between them. <c>"preDelayMs"</c> (default 0) sleeps
/// before the FIRST press — use it when chaining directly after AutoPaster's Ctrl+V so the
/// foreground window has time to consume the paste (~120 ms is the legacy 0.1.16 value).
/// </para>
/// </summary>
public sealed class PressKeyTask : IPipelineTask
{
    public const string TaskId = "arestoys.press-key";

    private readonly ILogger<PressKeyTask> _logger;

    public PressKeyTask(ILogger<PressKeyTask> logger)
    {
        _logger = logger;
    }

    public string Id => TaskId;
    public string DisplayName => "Press key";
    public PipelineTaskKind Kind => PipelineTaskKind.Both;

    public async Task ExecuteAsync(PipelineContext context, JsonNode? config, CancellationToken cancellationToken)
    {
        var (modifiers, vk, label) = ResolveCombo(config);
        if (vk == 0)
        {
            _logger.LogDebug("PressKeyTask: no key resolved from config — skipping.");
            return;
        }

        var count = Math.Max(1, (int?)config?["count"] ?? 1);
        var delayMs = Math.Max(0, (int?)config?["delayMs"] ?? 0);
        // Optional pre-delay before the FIRST press. Defaults to 0 — fires immediately. Set
        // to ~120 ms when chaining directly after AutoPaster's Ctrl+V to give the foreground
        // window time to consume the paste before the next keystroke lands (was the unconditional
        // 120 ms in 0.1.16 — moved to opt-in so Repeat loops + standalone hotkey-to-key remaps
        // don't pay 120 ms × every press of dead time).
        var preDelayMs = Math.Max(0, (int?)config?["preDelayMs"] ?? 0);
        if (preDelayMs > 0)
        {
            await Task.Delay(preDelayMs, cancellationToken).ConfigureAwait(false);
        }

        for (var i = 0; i < count; i++)
        {
            if (cancellationToken.IsCancellationRequested) break;
            await Application.Current.Dispatcher.InvokeAsync(() => SendComboOnce(modifiers, vk));
            if (i < count - 1 && delayMs > 0)
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogDebug("PressKeyTask: dispatched {Label} × {Count}", label, count);
    }

    /// <summary>Picks the (modifiers, vk) pair from whichever config shape the step carries.
    /// Priority: <c>combo</c> string → structured <c>vk</c>+<c>modifiers</c> → legacy
    /// <c>key</c>. Returns a printable label too so the debug log shows what actually fired
    /// regardless of which config shape sourced it.</summary>
    private static (HotkeyModifiers Modifiers, ushort Vk, string Label) ResolveCombo(JsonNode? config)
    {
        var combo = (string?)config?["combo"];
        if (!string.IsNullOrWhiteSpace(combo))
        {
            var parsed = KeyComboParser.Parse(combo);
            if (parsed is { } p) return (p.Modifiers, (ushort)p.VirtualKey, combo);
        }

        var structuredVk = (int?)config?["vk"];
        if (structuredVk is > 0 and <= 0xFF)
        {
            var mods = (HotkeyModifiers)(((int?)config?["modifiers"]) ?? 0);
            return (mods, (ushort)structuredVk.Value, HotkeyDisplay.Format(mods, (uint)structuredVk.Value));
        }

        var keyName = ((string?)config?["key"])?.ToLowerInvariant();
        if (keyName is not null)
        {
            var vk = keyName switch
            {
                "enter" or "return" or "newline" => AppNativeMethods.VkReturn,
                "tab" => AppNativeMethods.VkTab,
                _ => (ushort)0,
            };
            if (vk != 0) return (HotkeyModifiers.None, vk, keyName);
        }

        return (HotkeyModifiers.None, 0, "(none)");
    }

    /// <summary>Build + send a single press of <paramref name="modifiers"/> + <paramref name="vk"/>:
    /// modifier downs (Ctrl / Alt / Shift / Win in fixed order), action key down, action key up,
    /// modifier ups (reverse order). Sticky-modifier release runs first so a workflow triggered
    /// by Win+Shift+X doesn't have the held Win bleed into our synthetic combo (the
    /// <see cref="KeyInjector.ReleaseStickyModifiers"/> doc-comment expands on the race).</summary>
    private static void SendComboOnce(HotkeyModifiers modifiers, ushort vk)
    {
        KeyInjector.ReleaseStickyModifiers();

        var inputs = new List<AppNativeMethods.INPUT>(2 + 2 * 4);
        // Modifier downs in canonical Ctrl-first order — matches how most apps' shortcut tables
        // are written and avoids the rare case where the order materially changes interpretation
        // (some IMEs treat Shift-down-first as text-select intent).
        if ((modifiers & HotkeyModifiers.Control) != 0) inputs.Add(KeyInjector.MakeKey(AppNativeMethods.VkControl, false));
        if ((modifiers & HotkeyModifiers.Alt)     != 0) inputs.Add(KeyInjector.MakeKey(AppNativeMethods.VkMenu,    false));
        if ((modifiers & HotkeyModifiers.Shift)   != 0) inputs.Add(KeyInjector.MakeKey(AppNativeMethods.VkLShift,  false));
        if ((modifiers & HotkeyModifiers.Win)     != 0) inputs.Add(KeyInjector.MakeKey(AppNativeMethods.VkLWin,    false));

        inputs.Add(KeyInjector.MakeKey(vk, keyUp: false));
        inputs.Add(KeyInjector.MakeKey(vk, keyUp: true));

        // Reverse order on release so the modifier chord is symmetrical.
        if ((modifiers & HotkeyModifiers.Win)     != 0) inputs.Add(KeyInjector.MakeKey(AppNativeMethods.VkLWin,    true));
        if ((modifiers & HotkeyModifiers.Shift)   != 0) inputs.Add(KeyInjector.MakeKey(AppNativeMethods.VkLShift,  true));
        if ((modifiers & HotkeyModifiers.Alt)     != 0) inputs.Add(KeyInjector.MakeKey(AppNativeMethods.VkMenu,    true));
        if ((modifiers & HotkeyModifiers.Control) != 0) inputs.Add(KeyInjector.MakeKey(AppNativeMethods.VkControl, true));

        var arr = inputs.ToArray();
        AppNativeMethods.SendInput((uint)arr.Length, arr, Marshal.SizeOf<AppNativeMethods.INPUT>());
    }
}
