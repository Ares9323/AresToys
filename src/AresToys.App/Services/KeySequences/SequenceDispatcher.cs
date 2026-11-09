using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Extensions.Logging;
using AresToys.App.Native;

namespace AresToys.App.Services.KeySequences;

/// <summary>
/// Executes a confirmed binding: deletes the typed trigger characters from the foreground app
/// (Backspace × N via SendInput) and then either pastes the chosen clipboard item or runs the
/// bound workflow. The "capture foreground first, then backspace, then dispatch" ordering is
/// load-bearing — <see cref="AutoPaster"/> uses the captured hwnd to restore focus before its
/// Ctrl+V, and backspace events would otherwise race the focus.
/// </summary>
public sealed class SequenceDispatcher
{
    private readonly TargetWindowTracker _target;
    private readonly AutoPaster _paster;
    private readonly WorkflowRunner _workflowRunner;
    private readonly ILogger<SequenceDispatcher> _logger;

    public SequenceDispatcher(
        TargetWindowTracker target,
        AutoPaster paster,
        WorkflowRunner workflowRunner,
        ILogger<SequenceDispatcher> logger)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _paster = paster ?? throw new ArgumentNullException(nameof(paster));
        _workflowRunner = workflowRunner ?? throw new ArgumentNullException(nameof(workflowRunner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Dispatch a Replacer (paste a specific clipboard item). Cleans up <paramref name="backspaceCount"/>
    /// chars then asks AutoPaster to do its thing. Fire-and-forget — exceptions logged, not thrown.</summary>
    public async Task DispatchReplacerAsync(long itemId, int backspaceCount, CancellationToken cancellationToken)
    {
        _logger.LogInformation("KS-DEBUG: DispatchReplacerAsync itemId={Id} backspaceCount={N}.", itemId, backspaceCount);
        try
        {
            // No CaptureCurrentForeground: the overlay is non-activating so the foreground app
            // never lost focus. AutoPaster.PasteAsync is called with restoreForeground=false to
            // skip the SetForegroundWindow + Alt-tap dance — which would otherwise open Chrome /
            // Edge / Firefox menu bar via the fake Alt and swallow the Ctrl+V.
            SendBackspaces(backspaceCount);
            // Small settle so the foreground app processes the backspaces before AutoPaster
            // sets clipboard / sends Ctrl+V — without this, fast text fields can interleave the
            // events and end up with the trigger half-deleted.
            await Task.Delay(40, cancellationToken).ConfigureAwait(false);
            await _paster.PasteAsync(itemId, restoreForeground: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SequenceDispatcher: replacer dispatch failed for itemId={Id}", itemId);
        }
    }

    /// <summary>Dispatch a Workflow trigger. Cleans up trigger chars + the terminator (space/enter)
    /// then fires the workflow via <see cref="WorkflowRunner"/>. The workflow may launch a browser,
    /// open an app, etc. — we don't await it beyond the await-async kickoff because the user
    /// shouldn't wait at a frozen cursor for slow workflows.</summary>
    public async Task DispatchWorkflowAsync(string workflowId, int backspaceCount, CancellationToken cancellationToken)
    {
        try
        {
            SendBackspaces(backspaceCount);
            await Task.Delay(40, cancellationToken).ConfigureAwait(false);
            await _workflowRunner.RunAsync(workflowId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SequenceDispatcher: workflow dispatch failed for workflowId={Id}", workflowId);
        }
    }

    private void SendBackspaces(int count)
    {
        if (count <= 0) return;
        var inputs = new AppNativeMethods.INPUT[count * 2];
        for (var i = 0; i < count; i++)
        {
            inputs[i * 2] = MakeKey(AppNativeMethods.VkBack, keyUp: false);
            inputs[i * 2 + 1] = MakeKey(AppNativeMethods.VkBack, keyUp: true);
        }
        Application.Current.Dispatcher.Invoke(() =>
        {
            // Same modifier-release dance AutoPaster does — a hotkey-initiated dispatch may have
            // left modifiers physically held by the user. Without releasing them first our
            // Backspace events become Ctrl+Backspace (delete previous word) and chew too much.
            KeyInjector.ReleaseStickyModifiers();
            var sent = AppNativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<AppNativeMethods.INPUT>());
            if (sent != inputs.Length)
            {
                _logger.LogWarning("SequenceDispatcher: SendInput partial — sent {Sent}/{Expected} (target may be blocking injected input).", sent, inputs.Length);
            }
        });
    }

    private static AppNativeMethods.INPUT MakeKey(ushort virtualKey, bool keyUp) => new()
    {
        type = AppNativeMethods.InputKeyboard,
        u = new AppNativeMethods.InputUnion
        {
            ki = new AppNativeMethods.KEYBDINPUT
            {
                wVk = virtualKey,
                dwFlags = keyUp ? AppNativeMethods.KeyEventfKeyUp : 0,
            }
        }
    };
}
