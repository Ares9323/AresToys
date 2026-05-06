using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;

namespace ShareQ.App.Services;

/// <summary>Modern Windows toast notifier — emits toasts via WinRT
/// <c>ToastNotificationManager</c> (wrapped by <see cref="ToastNotificationManagerCompat"/> so
/// the unpackaged path Just Works). Toasts persist in the Notification Center after dismissal,
/// which is the headline reason to pick this over <see cref="WpfToastNotifier"/> /
/// <see cref="TrayToastNotifier"/>: users can scroll back through missed events instead of
/// losing them when the bubble fades.
///
/// AUMID handling: the compat layer registers an HKCU entry on first toast and attaches it to
/// the running process. Velopack's installer creates a Start Menu shortcut with the matching
/// AUMID, so installed copies show "ShareQ" as the source string in the Notification Center;
/// dev runs (bin/Release/Debug) end up under whatever the toolkit auto-derives from the
/// EXE path — fine for testing, slightly ugly in the UI.</summary>
public sealed class WindowsToastNotifier : IToastNotifier
{
    private readonly ILogger<WindowsToastNotifier> _logger;
    /// <summary>Per-toast callback set, keyed by the unique tag we attach as a toast argument.
    /// Each entry holds the optional body-click handler plus a button-index → handler map; the
    /// arrival of any click clears the entry (the toast dismisses regardless of which action
    /// fired, so a second activation can't reach the same toast). Replaces the old
    /// <c>_pendingClicks</c> dict which only supported a single body handler per toast.</summary>
    private readonly Dictionary<string, ToastCallbacks> _pendingToasts = new(StringComparer.Ordinal);
    private static int _nextTag;

    private sealed class ToastCallbacks
    {
        public Action? Body;
        public Dictionary<string, Action> Buttons { get; } = new(StringComparer.Ordinal);
    }

    public WindowsToastNotifier(ILogger<WindowsToastNotifier> logger)
    {
        _logger = logger;

        // Single global activation handler. The toolkit dispatches every click here; we route
        // by the toast's "toastTag" arg (a monotonic counter assigned at Show-time) into the
        // matching ToastCallbacks entry, then by "button" arg if a button was clicked.
        // Subscribing here is fine even if we never received a toast yet — the toolkit just
        // queues the handler.
        ToastNotificationManagerCompat.OnActivated += OnToastActivated;
    }

    public void Show(string title, string message, Action? onClick = null, string? imagePath = null,
                     IReadOnlyList<ToastButtonChoice>? buttons = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            var builder = new ToastContentBuilder()
                .AddText(title)
                .AddText(message);

            // Inline image preview — the toast template renders this between the title and
            // body, capped to ~3MB by Windows. We only attach when the file actually exists
            // (silent fallback if the save step hasn't completed yet) and when the path is
            // absolute, which the toast XML serializer requires.
            if (!string.IsNullOrEmpty(imagePath) && System.IO.Path.IsPathRooted(imagePath) && System.IO.File.Exists(imagePath))
            {
                try { builder.AddInlineImage(new Uri(imagePath)); }
                catch (Exception ex) { _logger.LogDebug(ex, "Toast inline image attach failed for {Path}; continuing without preview", imagePath); }
            }

            // Unique id used both as click-routing key and as ToastNotification.Tag/Group.
            // Two reasons to set Tag+Group per toast:
            //  1. Without distinct values, Windows replaces an existing toast with the same
            //     (Tag, Group) pair — the second of two near-simultaneous saves overwrites
            //     the first in the Notification Center, which made stacked captures vanish.
            //     Unique values let every toast persist independently.
            //  2. The OS de-dupe heuristic that occasionally suppresses repeat content from
            //     the same app keys off the same pair; flat "no two are equal" defeats it.
            var uid = System.Threading.Interlocked.Increment(ref _nextTag).ToString(System.Globalization.CultureInfo.InvariantCulture);

            var callbacks = new ToastCallbacks();
            if (onClick is not null)
            {
                callbacks.Body = onClick;
                // Body-click activation: just the toastTag, no "button" arg → router falls
                // through to the body handler.
                builder.AddArgument("toastTag", uid);
            }

            if (buttons is { Count: > 0 })
            {
                // Up to 5 buttons (Windows hard cap). Each button gets its own arg key
                // ("button=N") so the activation handler can distinguish them. The toastTag
                // arg is duplicated on every button — the click args inherit the top-level
                // ones too, but being explicit costs nothing and makes the intent obvious.
                for (var i = 0; i < buttons.Count && i < 5; i++)
                {
                    var idx = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    callbacks.Buttons[idx] = buttons[i].OnClick;
                    builder.AddButton(new ToastButton()
                        .SetContent(buttons[i].Label)
                        .AddArgument("toastTag", uid)
                        .AddArgument("button", idx));
                }
            }

            if (callbacks.Body is not null || callbacks.Buttons.Count > 0)
            {
                lock (_pendingToasts) _pendingToasts[uid] = callbacks;
            }

            // Show with a customizer to set Tag + Group on the WinRT ToastNotification before
            // it goes to the OS. Both are made unique per toast so the Notification Center
            // doesn't bucket them under a single "ShareQ — N items" entry: distinct Group
            // values appear as separate groups, distinct Tag values keep each toast from
            // replacing a sibling. Net effect is every toast sits on its own line in
            // Notification Center, which is what the user wants for chronological history.
            builder.Show(toast =>
            {
                toast.Tag = uid;
                toast.Group = uid;
            });
        }
        catch (Exception ex)
        {
            // Common failure modes: AUMID registration blocked by a managed env, the user has
            // disabled notifications globally, or this is running on a Windows SKU without the
            // Notifications service. None of these should crash the capture pipeline — log and
            // move on.
            _logger.LogWarning(ex, "Windows toast notification failed; capture pipeline continues");
        }
    }

    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        var args = ToastArguments.Parse(e.Argument);
        if (!args.TryGetValue("toastTag", out var tag)) return;

        Action? action = null;
        lock (_pendingToasts)
        {
            // First click on a toast wins: remove the whole entry so a second activation
            // (shouldn't happen — Windows dismisses the toast — but defensive) is a no-op.
            if (!_pendingToasts.Remove(tag, out var callbacks)) return;
            if (args.TryGetValue("button", out var idx) && callbacks.Buttons.TryGetValue(idx, out var btn))
                action = btn;
            else
                action = callbacks.Body;
        }
        if (action is null) return;
        try
        {
            // Marshal to UI thread — onClick handlers typically open windows / activate UI.
            // The toolkit fires OnActivated on a background thread when the app is already
            // running, which would crash any direct WPF call.
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(action);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Toast click handler threw");
        }
    }
}
