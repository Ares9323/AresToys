using System.Diagnostics;
using System.Text;
using System.Windows;
using Microsoft.Extensions.Logging;
using AresToys.App.Native;
using AresToys.Hotkeys;
using AresToys.Storage.Items;

namespace AresToys.App.Services.KeySequences;

/// <summary>
/// Rolling-buffer key tracker that consumes <see cref="KeyEvent"/>s from
/// <see cref="KeyboardHook.RegisterStreamListener"/> and decides when to open the overlay
/// (Replacer matches) or fire a workflow (Workflow matches on space/enter terminator).
/// State machine and decision rules live in the data-flow table of the design spec
/// (<c>docs/superpowers/specs/2026-05-16-key-sequences-design.md</c>, "Data flow tasto per tasto").
///
/// Threading: the stream listener fires on a ThreadPool worker (the hook marshals to avoid
/// blocking the kernel hook thread). All buffer / state mutations are guarded by
/// <see cref="_stateLock"/>. UI interactions (overlay show/close, atomic-binding register/
/// unregister, SendInput dispatch) are marshalled to the WPF dispatcher.
/// </summary>
public sealed class KeySequenceTracker : IDisposable
{
    private enum TrackerState { Idle, OverlayActive }

    // Overlay atomic-binding ids — see design spec "How suppression works during OverlayActive".
    private const string ConfirmBindingId = "keysequences.overlay.confirm";
    private const string CancelBindingId = "keysequences.overlay.cancel";
    private const string UpBindingId = "keysequences.overlay.up";
    private const string DownBindingId = "keysequences.overlay.down";

    private readonly KeyboardHook _hook;
    private readonly SequenceMatcher _matcher;
    private readonly IncognitoModeService _incognito;
    private readonly KeySequenceModuleSettings _settings;
    private readonly ModuleSettings _moduleSettings;
    private readonly ISequenceOverlay _overlay;
    private readonly SequenceDispatcher _dispatcher;
    private readonly IItemStore _items;
    private readonly ILogger<KeySequenceTracker> _logger;

    private readonly object _stateLock = new();
    private readonly StringBuilder _buffer = new(SequenceBinding.MaxLength);
    private TrackerState _state = TrackerState.Idle;
    private IntPtr _cachedForegroundHwnd = IntPtr.Zero;
    private bool _cachedForegroundBlacklisted;

    private Action<KeyEvent>? _streamListener;
    private bool _installed;

    public KeySequenceTracker(
        KeyboardHook hook,
        SequenceMatcher matcher,
        IncognitoModeService incognito,
        KeySequenceModuleSettings settings,
        ModuleSettings moduleSettings,
        ISequenceOverlay overlay,
        SequenceDispatcher dispatcher,
        IItemStore items,
        ILogger<KeySequenceTracker> logger)
    {
        _hook = hook ?? throw new ArgumentNullException(nameof(hook));
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        _incognito = incognito ?? throw new ArgumentNullException(nameof(incognito));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _moduleSettings = moduleSettings ?? throw new ArgumentNullException(nameof(moduleSettings));
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _incognito.StateChanged += OnIncognitoChanged;
    }

    public void ApplyEnabledState()
    {
        if (_moduleSettings.KeySequencesEnabled) Install();
        else Uninstall();
    }

    private void Install()
    {
        if (_installed) return;
        _streamListener = OnKey;
        _hook.RegisterStreamListener(_streamListener);
        _installed = true;
        _logger.LogInformation("KS-DEBUG: stream listener installed on KeyboardHook (instance hash={Hash}).", _hook.GetHashCode());
    }

    private void Uninstall()
    {
        if (!_installed) return;
        if (_streamListener is not null)
        {
            _hook.UnregisterStreamListener(_streamListener);
            _streamListener = null;
        }
        ResetBuffer(closeOverlay: true);
        _installed = false;
        _logger.LogInformation("KeySequenceTracker: stream listener uninstalled.");
    }

    private void OnKey(KeyEvent e)
    {
        if (!e.IsDown) return; // KEYUP irrelevant; overlay atomic bindings handle suppression
        if (_incognito.IsActive) return;

        // Foreground change → reset buffer, re-evaluate blacklist. Doing this per-keystroke is
        // O(syscall) — GetForegroundWindow is essentially free; the blacklist re-check only
        // does the heavy Process.GetProcessById call when the hwnd actually changed.
        var fg = AppNativeMethods.GetForegroundWindow();
        if (fg != _cachedForegroundHwnd)
        {
            _cachedForegroundHwnd = fg;
            _cachedForegroundBlacklisted = IsBlacklisted(fg);
            ResetBuffer(closeOverlay: true);
        }
        if (_cachedForegroundBlacklisted) return;

        // Modifiers other than Shift mean the user is doing a hotkey / shortcut, not typing a
        // word — reset and skip. Shift alone is fine (it's how capital letters happen).
        var nonShift = e.Modifiers & ~HotkeyModifiers.Shift;
        if (nonShift != HotkeyModifiers.None)
        {
            ResetBuffer(closeOverlay: false);
            return;
        }

        HandleKey(e);
    }

    private void HandleKey(KeyEvent e)
    {
        // When the overlay is open, the navigation/confirm/cancel keys belong to the overlay —
        // they're handled by atomic bindings (registered with suppress=true). The stream listener
        // sees them too (it's an observer that runs alongside atomic-binding dispatch), and if we
        // let it reach the switch below it would reset the buffer + close the overlay, racing the
        // atomic-binding callback that's trying to navigate/confirm. Result: arrows seem dead and
        // Enter finds the confirm callback already null'd by the dismiss path.
        if (_state == TrackerState.OverlayActive)
        {
            if (e.VkCode == AppNativeMethods.VkUp
                || e.VkCode == AppNativeMethods.VkDown
                || e.VkCode == AppNativeMethods.VkEscape
                || e.VkCode == AppNativeMethods.VkReturn
                || e.VkCode == _settings.ConfirmVk)
            {
                return;
            }
        }

        switch (e.VkCode)
        {
            case AppNativeMethods.VkBack:
                HandleBackspace();
                return;
            case AppNativeMethods.VkReturn:
                HandleTerminator();
                return;
            case AppNativeMethods.VkTab:
            case AppNativeMethods.VkEscape:
            case AppNativeMethods.VkUp:
            case AppNativeMethods.VkDown:
            case 0x25: // VK_LEFT
            case 0x27: // VK_RIGHT
            case 0x24: // VK_HOME
            case 0x23: // VK_END
            case 0x21: // VK_PAGEUP
            case 0x22: // VK_PAGEDOWN
            case 0x2E: // VK_DELETE
                ResetBuffer(closeOverlay: true);
                return;
        }

        // F-keys (F1..F24) are 0x70..0x87 — never part of a typed word.
        if (e.VkCode >= 0x70 && e.VkCode <= 0x87)
        {
            ResetBuffer(closeOverlay: true);
            return;
        }

        var c = e.PrintableChar;
        if (c is null)
        {
            // Non-printable, not in our switch — modifier keys alone, media keys, etc. Don't
            // reset (a Shift down event should not destroy the in-progress buffer).
            return;
        }

        if (c == ' ')
        {
            HandleTerminator();
            return;
        }

        if (IsSequenceChar(c.Value))
        {
            AppendAndMatch(c.Value);
            return;
        }

        // Any other printable (punctuation, accented letters, symbols) breaks the sequence.
        ResetBuffer(closeOverlay: true);
    }

    private static bool IsSequenceChar(char c)
        => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';

    private void AppendAndMatch(char c)
    {
        string snapshot;
        lock (_stateLock)
        {
            if (_buffer.Length >= SequenceBinding.MaxLength)
            {
                // Drop oldest char to keep a rolling window. Bigger triggers don't make sense and
                // would let arbitrary typing pin matcher memory.
                _buffer.Remove(0, 1);
            }
            _buffer.Append(c);
            snapshot = _buffer.ToString();
        }

        var matches = _matcher.Match(snapshot);
        _logger.LogInformation("KS-DEBUG: buffer='{Buf}' matches={N} (matcher.BindingCount={Total}).", snapshot, matches.Count, _matcher.BindingCount);
        if (matches.Count == 0)
        {
            // Buffer continues to grow; if an overlay was open it's now stale — close it.
            if (_state == TrackerState.OverlayActive) CloseOverlay();
            return;
        }

        var replacers = matches.Where(b => b.Target is ReplaceWithItem).ToList();
        if (replacers.Count > 0)
        {
            _logger.LogInformation("KS-DEBUG: opening overlay for {N} replacer(s), sequence='{Buf}'.", replacers.Count, snapshot);
            OpenOverlayForReplacers(replacers, snapshot.Length);
        }
        // Workflow matches don't open an overlay; they wait for a terminator (space/enter).
    }

    private void HandleBackspace()
    {
        int newLength;
        lock (_stateLock)
        {
            if (_buffer.Length == 0) return;
            _buffer.Length--;
            newLength = _buffer.Length;
        }
        if (newLength == 0)
        {
            if (_state == TrackerState.OverlayActive) CloseOverlay();
            return;
        }
        // After shrink, re-query — the new (shorter) buffer might match a different sequence.
        var snapshot = SnapshotBuffer();
        var matches = _matcher.Match(snapshot);
        var replacers = matches.Where(b => b.Target is ReplaceWithItem).ToList();
        if (replacers.Count > 0) OpenOverlayForReplacers(replacers, snapshot.Length);
        else if (_state == TrackerState.OverlayActive) CloseOverlay();
    }

    private void HandleTerminator()
    {
        string snapshot;
        int bufferLength;
        lock (_stateLock)
        {
            if (_buffer.Length == 0)
            {
                // Terminator on empty buffer is a no-op — just normal whitespace typing.
                return;
            }
            snapshot = _buffer.ToString();
            bufferLength = _buffer.Length;
            _buffer.Clear();
        }

        if (_state == TrackerState.OverlayActive)
        {
            // Enter while overlay open is "confirm". The atomic-binding callback already handles
            // this path; if we somehow got here via the stream listener (e.g. confirm key isn't
            // bound to Enter), just close the overlay.
            CloseOverlay();
            return;
        }

        var matches = _matcher.Match(snapshot);
        var workflow = matches.OfType<SequenceBinding>().FirstOrDefault(b => b.Target is RunWorkflow);
        if (workflow is null) return;

        var workflowId = ((RunWorkflow)workflow.Target).WorkflowId;
        _logger.LogDebug("KeySequenceTracker: dispatching workflow '{Id}' for sequence '{Seq}'.", workflowId, snapshot);
        // bufferLength + 1 = the trigger chars + the terminator key (space/enter). The dispatcher
        // backspaces all of them so the trigger text doesn't pollute the foreground.
        _ = _dispatcher.DispatchWorkflowAsync(workflowId, bufferLength + 1, CancellationToken.None);
    }

    private void ResetBuffer(bool closeOverlay)
    {
        lock (_stateLock) _buffer.Clear();
        if (closeOverlay && _state == TrackerState.OverlayActive) CloseOverlay();
    }

    private string SnapshotBuffer()
    {
        lock (_stateLock) return _buffer.ToString();
    }

    // --- Overlay lifecycle ---

    private void OpenOverlayForReplacers(IReadOnlyList<SequenceBinding> replacers, int sequenceLength)
    {
        var options = new List<OverlayOption>();
        foreach (var b in replacers)
        {
            if (b.Target is not ReplaceWithItem r) continue;
            var preview = TryGetItemPreview(r.ItemId);
            options.Add(new OverlayOption(r.ItemId, preview));
        }
        if (options.Count == 0) return;

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            // If we were already showing, dispose the previous overlay state cleanly first so
            // the atomic bindings get re-registered fresh (confirm-key could've changed mid-flight).
            if (_state == TrackerState.OverlayActive) CloseOverlay_OnUi();
            _state = TrackerState.OverlayActive;
            RegisterOverlayAtomicBindings(sequenceLength);
            _overlay.Show(options,
                onConfirm: chosen => OnOverlayConfirmed(chosen, sequenceLength),
                onDismiss: OnOverlayDismissed);
        });
    }

    private void CloseOverlay()
        => Application.Current.Dispatcher.BeginInvoke(CloseOverlay_OnUi);

    private void CloseOverlay_OnUi()
    {
        if (_state != TrackerState.OverlayActive) return;
        _state = TrackerState.Idle;
        UnregisterOverlayAtomicBindings();
        _overlay.Close();
    }

    private void OnOverlayConfirmed(OverlayOption chosen, int sequenceLength)
    {
        _logger.LogInformation("KS-DEBUG: OnOverlayConfirmed itemId={Id} seqLen={Len} state={State}.", chosen.ItemId, sequenceLength, _state);
        if (_state != TrackerState.OverlayActive) return;
        _state = TrackerState.Idle;
        UnregisterOverlayAtomicBindings();
        lock (_stateLock) _buffer.Clear();
        // Close the overlay window — the confirm path bypasses CloseOverlay_OnUi (which is what
        // normally hides it on Esc / click-outside / typed-past-match), so without this the
        // overlay stays visible forever after a successful pick.
        if (_overlay.IsVisible) _overlay.Close();
        _ = _dispatcher.DispatchReplacerAsync(chosen.ItemId, sequenceLength, CancellationToken.None);
    }

    private void OnOverlayDismissed()
    {
        if (_state != TrackerState.OverlayActive) return;
        _state = TrackerState.Idle;
        UnregisterOverlayAtomicBindings();
        // Buffer intentionally not cleared — user may keep typing and form a longer trigger.
        // Foreground-change / boundary keys will reset it as usual.
    }

    private void RegisterOverlayAtomicBindings(int sequenceLength)
    {
        var confirmVk = _settings.ConfirmVk;
        _logger.LogInformation("KS-DEBUG: registering overlay atomic bindings (confirm=0x{Vk:X2}, up=0x26, down=0x28, esc=0x1B).", confirmVk);
        _hook.Register(ConfirmBindingId, HotkeyModifiers.None, confirmVk, () =>
        {
            _logger.LogInformation("KS-DEBUG: confirm key fired.");
            Application.Current.Dispatcher.BeginInvoke(_overlay.ConfirmCurrent);
        }, suppress: true);
        _hook.Register(CancelBindingId, HotkeyModifiers.None, AppNativeMethods.VkEscape, () =>
        {
            _logger.LogInformation("KS-DEBUG: cancel (Esc) fired.");
            Application.Current.Dispatcher.BeginInvoke(CloseOverlay_OnUi);
        }, suppress: true);
        _hook.Register(UpBindingId, HotkeyModifiers.None, AppNativeMethods.VkUp, () =>
        {
            _logger.LogInformation("KS-DEBUG: Up arrow fired.");
            Application.Current.Dispatcher.BeginInvoke(_overlay.SelectPrevious);
        }, suppress: true);
        _hook.Register(DownBindingId, HotkeyModifiers.None, AppNativeMethods.VkDown, () =>
        {
            _logger.LogInformation("KS-DEBUG: Down arrow fired.");
            Application.Current.Dispatcher.BeginInvoke(_overlay.SelectNext);
        }, suppress: true);
    }

    private void UnregisterOverlayAtomicBindings()
    {
        _hook.Unregister(ConfirmBindingId);
        _hook.Unregister(CancelBindingId);
        _hook.Unregister(UpBindingId);
        _hook.Unregister(DownBindingId);
    }

    // --- Item preview lookup ---

    private string TryGetItemPreview(long itemId)
    {
        try
        {
            var record = _items.GetByIdAsync(itemId, CancellationToken.None).GetAwaiter().GetResult();
            if (record is null) return $"#{itemId} (missing)";
            if (!string.IsNullOrWhiteSpace(record.Label)) return record.Label!;
            if (!string.IsNullOrWhiteSpace(record.SearchText)) return Truncate(record.SearchText!, 80);
            return $"#{itemId} ({record.Kind})";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KeySequenceTracker: preview lookup failed for itemId={Id}", itemId);
            return $"#{itemId}";
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // --- Blacklist ---

    private bool IsBlacklisted(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            AppNativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return false;
            using var proc = Process.GetProcessById((int)pid);
            var name = proc.ProcessName; // sans .exe
            foreach (var blocked in _settings.Blacklist)
            {
                if (string.IsNullOrEmpty(blocked)) continue;
                var withoutExt = blocked.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? blocked[..^4]
                    : blocked;
                if (string.Equals(name, withoutExt, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch
        {
            // Process may have exited between GetForegroundWindow and GetProcessById, or the pid
            // may belong to a protected process we can't inspect. Either way: not blacklisted.
        }
        return false;
    }

    // --- Reactive handlers ---

    private void OnIncognitoChanged(object? sender, EventArgs e)
    {
        if (_incognito.IsActive) ResetBuffer(closeOverlay: true);
    }

    public void Dispose()
    {
        _incognito.StateChanged -= OnIncognitoChanged;
        Uninstall();
    }
}
