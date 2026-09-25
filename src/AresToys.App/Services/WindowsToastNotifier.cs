using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;

namespace AresToys.App.Services;

/// <summary>Modern Windows toast notifier — emits toasts via WinRT
/// <c>ToastNotificationManager</c> (wrapped by <see cref="ToastNotificationManagerCompat"/> so
/// the unpackaged path Just Works). Toasts persist in the Notification Center after dismissal,
/// which is the headline reason to pick this over <see cref="WpfToastNotifier"/> /
/// <see cref="TrayToastNotifier"/>: users can scroll back through missed events instead of
/// losing them when the bubble fades.
///
/// AUMID handling: the compat layer registers an HKCU entry on first toast and attaches it to
/// the running process. Velopack's installer creates a Start Menu shortcut with the matching
/// AUMID, so installed copies show "AresToys" as the source string in the Notification Center;
/// dev runs (bin/Release/Debug) end up under whatever the toolkit auto-derives from the
/// EXE path — fine for testing, slightly ugly in the UI.</summary>
public sealed class WindowsToastNotifier : IToastNotifier
{
    private readonly Notifications.ToastLifetimeService _lifetime;
    private readonly ILogger<WindowsToastNotifier> _logger;
    /// <summary>Per-toast callback set, keyed by the unique tag we attach as a toast argument.
    /// Each entry holds the optional body-click handler plus a button-index → handler map; the
    /// arrival of any click clears the entry (the toast dismisses regardless of which action
    /// fired, so a second activation can't reach the same toast). Replaces the old
    /// <c>_pendingClicks</c> dict which only supported a single body handler per toast.</summary>
    private readonly Dictionary<string, ToastCallbacks> _pendingToasts = new(StringComparer.Ordinal);
    private static int _nextTag;

    /// <summary>Toasts whose popup is (or may still be) on screen, keyed by uid. Added at Show
    /// when a popup is requested, removed on <c>Dismissed</c> (timeout, user close, our own
    /// Hide) or activation. Everything <see cref="HideOnScreenPopupsAsync"/> needs to take a
    /// popup down and put the entry back in the Center.</summary>
    private readonly Dictionary<string, OnScreenToast> _onScreen = new(StringComparer.Ordinal);

    private sealed record OnScreenToast(
        ToastContentBuilder Builder,
        Windows.UI.Notifications.ToastNotification Shown,
        int CenterSeconds,
        DateTimeOffset? ExpiresAt);

    /// <summary>Opt-in (Capture settings): take our popups down before a capture. Off by default
    /// because the shell needs time to actually remove the banner, and that wait is latency on
    /// every shot that finds one on screen.</summary>
    public const string HideBeforeCaptureKey = "capture.hide_toasts_before_capture";

    /// <summary>How long to wait after Hide before the screen is grabbed, in ms. 60 ms was not
    /// enough on the test machine, nor was 150; 200 is the default and users tune it to their PC.</summary>
    public const string HideBeforeCaptureDelayKey = "capture.hide_toasts_delay_ms";
    public const int DefaultHideBeforeCaptureDelayMs = 200;
    public const int MaxHideBeforeCaptureDelayMs = 2000;

    private sealed class ToastCallbacks
    {
        public Action? Body;
        public Dictionary<string, Action> Buttons { get; } = new(StringComparer.Ordinal);
    }

    private readonly AresToys.Storage.Settings.ISettingsStore _settings;

    public WindowsToastNotifier(Notifications.ToastLifetimeService lifetime, AresToys.Storage.Settings.ISettingsStore settings, ILogger<WindowsToastNotifier> logger)
    {
        _lifetime = lifetime;
        _settings = settings;
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

        // "No popup AND no Notification Center entry" is a toast nobody could ever see. Doing the
        // work anyway would only cost a round-trip through the OS notification service.
        if (_lifetime.NotificationsEffectivelyInvisible)
        {
            _logger.LogDebug("Toast suppressed: lifetime settings ask for neither a popup nor a Center entry");
            return;
        }

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
            // doesn't bucket them under a single "AresToys — N items" entry: distinct Group
            // values appear as separate groups, distinct Tag values keep each toast from
            // replacing a sibling. Net effect is every toast sits on its own line in
            // Notification Center, which is what the user wants for chronological history.
            var popupSeconds = _lifetime.PopupSeconds;
            var centerSeconds = _lifetime.CenterSeconds;

            // One absolute instant, computed once and reused if the toast has to be re-issued
            // below — otherwise a re-issue would silently extend the Center lifetime.
            DateTimeOffset? expiresAt = centerSeconds > 0
                ? DateTimeOffset.Now.AddSeconds(centerSeconds)
                : null;
            Windows.UI.Notifications.ToastNotification? shown = null;

            builder.Show(toast =>
            {
                toast.Tag = uid;
                toast.Group = uid;
                shown = toast;

                // Popup = 0: nothing on screen, straight into the Notification Center.
                if (popupSeconds == Notifications.ToastLifetimeService.PopupNone) toast.SuppressPopup = true;

                // Center = N seconds: Windows drops the entry itself at that moment, whether or
                // not AresToys is still running. Center = -1 (unlimited) sets no expiry at all.
                if (expiresAt is { } expiry) toast.ExpirationTime = expiry;

                // Center = 0: the popup closing is exactly when the entry should go.
                if (centerSeconds == Notifications.ToastLifetimeService.CenterNone)
                    toast.Dismissed += (_, _) => RemoveFromHistory(uid);

                // Track the popup until it leaves the screen, so a capture can take it down.
                if (!toast.SuppressPopup)
                    toast.Dismissed += (_, _) => { lock (_onScreen) _onScreen.Remove(uid); };
            });
            if (shown is not null && !shown.SuppressPopup)
            {
                lock (_onScreen) _onScreen[uid] = new OnScreenToast(builder, shown, centerSeconds, expiresAt);
            }

            // Only when the user asked for a popup SHORTER than Windows' own duration. At the
            // ceiling we leave the OS alone so a longer "show notifications for" accessibility
            // setting keeps working.
            if (_lifetime.ClosesPopupEarly && shown is not null)
                ScheduleEarlyPopupClose(builder, shown, uid, popupSeconds, centerSeconds, expiresAt);

            // Belt and braces on the Center lifetime. ExpirationTime alone is accurate (measured
            // to a tenth of a second) but it is the OS quietly dropping the entry, and the
            // Notification Center's own list doesn't necessarily redraw until it's reopened. An
            // explicit Remove at the same moment is a second, louder signal — it costs nothing
            // and only runs while AresToys is alive; ExpirationTime still covers the rest.
            if (expiresAt is { } dropAt) ScheduleCenterRemoval(uid, dropAt);
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

    /// <summary>Close the popup after <paramref name="popupSeconds"/> instead of letting Windows
    /// decide. There's no API for "show this popup for N seconds", so we call
    /// <c>ToastNotifier.Hide</c> — which, measured, also drops the Notification Center entry. When
    /// the user still wants a Center entry we immediately re-issue the same content with
    /// <c>SuppressPopup</c>, which lands in the Center without flashing a second popup.</summary>
    private void ScheduleEarlyPopupClose(
        ToastContentBuilder builder,
        Windows.UI.Notifications.ToastNotification shown,
        string uid,
        int popupSeconds,
        int centerSeconds,
        DateTimeOffset? expiresAt)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(popupSeconds)).ConfigureAwait(false);
                // A capture may already have taken this popup down (and re-issued the entry).
                bool stillOnScreen;
                lock (_onScreen) stillOnScreen = _onScreen.Remove(uid);
                if (!stillOnScreen) return;
                HideKeepingCenterEntry(builder, shown, uid, centerSeconds, expiresAt);
            }
            catch (Exception ex)
            {
                // A toast that outlives its configured popup duration is a cosmetic problem; it
                // must never surface as an unhandled exception on a background thread.
                _logger.LogDebug(ex, "Early popup close failed for toast {Uid}", uid);
            }
        });
    }

    public async Task HideOnScreenPopupsAsync()
    {
        // Nothing on screen → no settings read, no wait: captures stay instant.
        lock (_onScreen) { if (_onScreen.Count == 0) return; }
        if (await _settings.GetAsync(HideBeforeCaptureKey, CancellationToken.None).ConfigureAwait(false) != "1") return;
        var rawDelay = await _settings.GetAsync(HideBeforeCaptureDelayKey, CancellationToken.None).ConfigureAwait(false);
        var delayMs = int.TryParse(rawDelay, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? Math.Clamp(d, 0, MaxHideBeforeCaptureDelayMs)
            : DefaultHideBeforeCaptureDelayMs;

        KeyValuePair<string, OnScreenToast>[] toHide;
        lock (_onScreen)
        {
            toHide = _onScreen.ToArray();
            _onScreen.Clear();
        }
        if (toHide.Length == 0) return;

        foreach (var (uid, t) in toHide)
        {
            try { HideKeepingCenterEntry(t.Builder, t.Shown, uid, t.CenterSeconds, t.ExpiresAt); }
            catch (Exception ex) { _logger.LogDebug(ex, "Hiding toast {Uid} before capture failed", uid); }
        }
        _logger.LogDebug("Hid {Count} toast popup(s) before capture", toHide.Length);
        if (delayMs > 0) await Task.Delay(delayMs).ConfigureAwait(false);
    }

    /// <summary><c>ToastNotifier.Hide</c> takes the popup down but also drops the Center entry;
    /// when the user still wants the entry, re-issue the same content with <c>SuppressPopup</c>
    /// (lands in the Center without a second popup). Same (Tag, Group) so it replaces nothing
    /// else, same absolute expiry so the Center lifetime isn't extended.</summary>
    private static void HideKeepingCenterEntry(
        ToastContentBuilder builder,
        Windows.UI.Notifications.ToastNotification shown,
        string uid,
        int centerSeconds,
        DateTimeOffset? expiresAt)
    {
        ToastNotificationManagerCompat.CreateToastNotifier().Hide(shown);

        // Center = 0 ("don't keep") → Hide already did the whole job.
        if (centerSeconds == Notifications.ToastLifetimeService.CenterNone) return;
        // Expiry already passed while the popup was up → nothing to put back.
        if (expiresAt is { } expiry && expiry <= DateTimeOffset.Now) return;

        builder.Show(toast =>
        {
            toast.Tag = uid;
            toast.Group = uid;
            toast.SuppressPopup = true;
            if (expiresAt is { } e) toast.ExpirationTime = e;
        });
    }

    /// <summary>Remove the Center entry ourselves when its configured lifetime runs out, rather
    /// than relying solely on the OS honouring <c>ExpirationTime</c>.</summary>
    private void ScheduleCenterRemoval(string uid, DateTimeOffset dropAt)
    {
        _ = Task.Run(async () =>
        {
            var wait = dropAt - DateTimeOffset.Now;
            if (wait > TimeSpan.Zero) await Task.Delay(wait).ConfigureAwait(false);
            RemoveFromHistory(uid);
        });
    }

    /// <summary>Drop a toast from the Notification Center by tag+group.</summary>
    private void RemoveFromHistory(string uid)
    {
        try { ToastNotificationManagerCompat.History.Remove(uid, uid); }
        catch (Exception ex) { _logger.LogDebug(ex, "Removing toast {Uid} from the Notification Center failed", uid); }
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
            lock (_onScreen) _onScreen.Remove(tag);
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
